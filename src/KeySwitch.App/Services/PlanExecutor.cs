using System.Collections.Concurrent;
using System.Diagnostics;
using KeySwitch.Core.Correction;
using KeySwitch.Core.Input;
using KeySwitch.Data;
using KeySwitch.Hooks;

namespace KeySwitch.App.Services;

/// <summary>Одно нажатие, отложенное на время выполнения плана (для последующего воспроизведения).</summary>
public readonly record struct KeyReplayItem(
    int VirtualKey,
    int ScanCode,
    bool Shift,
    bool Ctrl,
    bool Alt,
    bool Win,
    bool CapsLock,
    bool IsKeyDown,
    char? Character);

/// <summary>
/// Выполнение планов исправления в фоне и очередь пользовательских нажатий.
///
/// Зачем очередь: пока мы удаляем слово и печатаем исправленное, пользователь может
/// успеть нажать следующую клавишу. Такие нажатия перехватываются хуком (Handled = true),
/// складываются в очередь и воспроизводятся через SendInput сразу после исправления —
/// быстрый набор не теряется.
/// Поток выполнения — STA: в нём же работают буфер обмена и SoundPlayer-подобные API.
/// </summary>
public sealed class PlanExecutor : IDisposable
{
    private const int VkControl = KeyClassifier.VkControl;
    private const int VkMenu = KeyClassifier.VkMenu;
    private const int VkLWin = KeyClassifier.VkLWin;

    private readonly InputSender _sender;
    private readonly LayoutSwitcher _layouts;
    private readonly AppLogger _logger;
    private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>());
    private readonly ConcurrentQueue<KeyReplayItem> _blocked = new();
    private readonly object _sync = new();
    private readonly Thread _worker;
    private volatile bool _busy;
    private volatile bool _stopping;
    private bool _disposed;

    public PlanExecutor(InputSender sender, LayoutSwitcher layouts, AppLogger logger)
    {
        _sender = sender;
        _layouts = layouts;
        _logger = logger;
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "KeySwitch.PlanExecutor"
        };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    /// <summary>Идёт выполнение плана; хук в это время перехватывает нажатия.</summary>
    public bool IsBusy => _busy;

    /// <summary>
    /// Пытается отложить нажатие. true — нажатие перехвачено (Handled = true)
    /// и будет воспроизведено после исправления.
    /// </summary>
    public bool TryBlockKey(KeyReplayItem item)
    {
        lock (_sync)
        {
            if (!_busy)
                return false;

            _blocked.Enqueue(item);
            return true;
        }
    }

    /// <summary>
    /// Ставит в очередь исправление слова (с переключением раскладки).
    /// <paramref name="processName"/> нужен только для журнала и выбора паузы.
    /// </summary>
    public void ExecutePlan(CorrectionPlan plan, IntPtr targetWindow, string processName = "")
        => ExecuteExclusive(() => ExecutePlanNow(plan, targetWindow, processName));

    /// <summary>
    /// Немедленное исправление на текущем потоке. Вызывать только с потока PlanExecutor
    /// (изнутри <see cref="ExecuteExclusive"/>) — иначе нарушится очередь нажатий.
    ///
    /// Порядок (требование ТЗ): пробел пользователя уже ушёл в окно → Backspace × (слово + 1)
    /// → смена раскладки → слово и пробел. Для браузеров и Electron между стиранием и
    /// вводом выдерживается пауза, иначе часть Backspace теряется.
    /// </summary>
    public void ExecutePlanNow(CorrectionPlan plan, IntPtr targetWindow, string processName = "")
    {
        var watch = Stopwatch.StartNew();
        string proc = string.IsNullOrWhiteSpace(processName) ? "?" : processName;
        int pauseMs = CorrectionPacing.PauseFor(processName);
        string layoutBefore = SafeLayoutId(targetWindow);

        _logger.Info(
            $"Исправление: старт proc={proc} hwnd=0x{targetWindow.ToInt64():X} " +
            $"backspaces={plan.DeleteCount} target={plan.TargetLayoutId} " +
            $"layout_before={layoutBefore} pause={pauseMs}мс conf={plan.Confidence:0.00}");

        try
        {
            if (pauseMs > 0)
            {
                // Двухфазный режим: стереть → сменить раскладку → пауза → напечатать.
                var erase = _sender.SendBackspaces(plan.DeleteCount);
                LogSend("Backspace", erase);

                bool switched = EnsureLayout(targetWindow, plan);
                LogLayout(targetWindow, layoutBefore, switched);

                Thread.Sleep(pauseMs);

                var typing = _sender.SendTextAndTerminator(plan.ReplacementText, plan.Terminator);
                LogSend("ввод", typing);
            }
            else
            {
                // Обычный режим: раскладка меняется до пакета, потому что текст уходит
                // Unicode-символами и от раскладки не зависит, а все события должны
                // попасть в окно одним SendInput.
                bool switched = EnsureLayout(targetWindow, plan);
                LogLayout(targetWindow, layoutBefore, switched);

                var result = _sender.SendCorrection(
                    plan.DeleteCount, plan.ReplacementText, plan.Terminator, pauseMs: 0);
                LogSend("Backspace+ввод", result);
            }

            _logger.Info($"Исправление: завершено за {watch.Elapsed.TotalMilliseconds:0} мс");
        }
        catch (Exception ex)
        {
            _logger.Error(
                $"Исправление: исключение proc={proc} hwnd=0x{targetWindow.ToInt64():X} " +
                $"backspaces={plan.DeleteCount} за {watch.Elapsed.TotalMilliseconds:0} мс", ex);
            throw;
        }
    }

    /// <summary>Ставит в очередь отмену исправления.</summary>
    public void ExecuteUndo(UndoPlan plan, IntPtr targetWindow)
        => ExecuteExclusive(() => ExecuteUndoNow(plan, targetWindow));

    /// <summary>Немедленная отмена на текущем потоке (только с потока PlanExecutor).</summary>
    public void ExecuteUndoNow(UndoPlan plan, IntPtr targetWindow)
    {
        var watch = Stopwatch.StartNew();
        string layoutBefore = SafeLayoutId(targetWindow);
        _logger.Info(
            $"Отмена: старт hwnd=0x{targetWindow.ToInt64():X} backspaces={plan.DeleteCount} " +
            $"restore={plan.RestoreLayoutId ?? "-"} layout_before={layoutBefore}");

        try
        {
            if (!string.IsNullOrWhiteSpace(plan.RestoreLayoutId) &&
                !string.Equals(plan.RestoreLayoutId, plan.CurrentLayoutId, StringComparison.OrdinalIgnoreCase))
            {
                bool switched = _layouts.SwitchTo(targetWindow, plan.RestoreLayoutId);
                LogLayout(targetWindow, layoutBefore, switched);
            }

            var result = _sender.SendCorrection(
                plan.DeleteCount, plan.RestoreText, plan.Terminator, pauseMs: 0);
            LogSend("Отмена: Backspace+ввод", result);

            _logger.Info($"Отмена: завершено за {watch.Elapsed.TotalMilliseconds:0} мс");
        }
        catch (Exception ex)
        {
            _logger.Error($"Отмена: исключение за {watch.Elapsed.TotalMilliseconds:0} мс", ex);
            throw;
        }
    }

    /// <summary>Произвольная работа, во время которой нажатия пользователя откладываются.</summary>
    public void ExecuteExclusive(Action work)
    {
        if (_disposed)
            return;

        lock (_sync)
        {
            _busy = true;
        }

        try
        {
            _queue.Add(work);
        }
        catch (InvalidOperationException)
        {
            // Очередь уже закрыта (выход из приложения).
            lock (_sync)
            {
                _busy = false;
            }
        }
    }

    private void WorkerLoop()
    {
        try
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    _logger.Error("Ошибка выполнения плана исправления", ex);
                }

                ReplayBlockedKeys();
            }
        }
        catch (ObjectDisposedException)
        {
            // Нормальное завершение.
        }
        catch (Exception ex)
        {
            _logger.Error("Поток выполнения планов упал", ex);
        }
    }

    /// <summary>
    /// Воспроизводит нажатия, накопившиеся за время работы плана, и только потом
    /// снимает флаг занятости — так между «выполнили» и «разрешили ввод» нет окна потери.
    /// </summary>
    private void ReplayBlockedKeys()
    {
        while (!_stopping)
        {
            KeyReplayItem[] batch;
            lock (_sync)
            {
                if (_blocked.IsEmpty)
                {
                    _busy = false;
                    return;
                }

                batch = _blocked.ToArray();
                while (_blocked.TryDequeue(out _))
                {
                }
            }

            Replay(batch);
        }

        lock (_sync)
        {
            _busy = false;
        }
    }

    private void Replay(KeyReplayItem[] batch)
    {
        foreach (var item in batch)
        {
            if (!item.IsKeyDown)
                continue; // клик воспроизводим по нажатию: keydown + keyup

            var ctrl = false;
            var alt = false;
            var win = false;
            try
            {
                if (item.Ctrl)
                {
                    _sender.SendKeyStroke(VkControl, false, keyUp: false);
                    ctrl = true;
                }

                if (item.Alt)
                {
                    _sender.SendKeyStroke(VkMenu, false, keyUp: false);
                    alt = true;
                }

                if (item.Win)
                {
                    _sender.SendKeyStroke(VkLWin, false, keyUp: false);
                    win = true;
                }

                ReplaySingle(item);
            }
            catch (Exception ex)
            {
                _logger.Debug($"Воспроизведение нажатия: {ex.Message}");
            }
            finally
            {
                if (win)
                    _sender.SendKeyStroke(VkLWin, false, keyUp: true);
                if (alt)
                    _sender.SendKeyStroke(VkMenu, false, keyUp: true);
                if (ctrl)
                    _sender.SendKeyStroke(VkControl, false, keyUp: true);
            }
        }
    }

    private void ReplaySingle(KeyReplayItem item)
    {
        // Символы, которые нельзя отправить как нажатие виртуальной клавиши (буквы/цифры
        // раскладка преобразует сама), отправляем через Unicode-ввод.
        if (item.Character is { } ch && !char.IsLetterOrDigit(ch) && !char.IsControl(ch) &&
            item.VirtualKey is not (KeyClassifier.VkSpace or KeyClassifier.VkReturn or KeyClassifier.VkTab))
        {
            _sender.SendText(ch.ToString());
            return;
        }

        _sender.SendKeyStroke(item.VirtualKey, item.Shift, keyUp: false);
        _sender.SendKeyStroke(item.VirtualKey, item.Shift, keyUp: true);
    }

    // ------------------------------------------------------------ журнал

    private void LogSend(string step, SendResult result)
    {
        string message =
            $"Исправление: {step} отправлено={result.Sent} принято={result.Inserted} " +
            $"ошибка={result.LastError}";

        if (result.Complete)
            _logger.Info(message);
        else
            _logger.Warn(message + " (часть событий не принята)");
    }

    private void LogLayout(IntPtr window, string layoutBefore, bool switched)
    {
        string layoutAfter = SafeLayoutId(window);
        _logger.Info(
            $"Исправление: раскладка {layoutBefore} -> {layoutAfter} ok={switched}");
    }

    private bool EnsureLayout(IntPtr window, CorrectionPlan plan)
    {
        if (string.Equals(plan.TargetLayoutId, plan.SourceLayoutId, StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            return _layouts.SwitchTo(window, plan.TargetLayoutId);
        }
        catch (Exception ex)
        {
            _logger.Error($"Исправление: смена раскладки на {plan.TargetLayoutId} не удалась", ex);
            return false;
        }
    }

    private string SafeLayoutId(IntPtr window)
    {
        try
        {
            return _layouts.GetLayoutId(window);
        }
        catch (Exception ex)
        {
            _logger.Debug($"GetLayoutId: {ex.Message}");
            return "?";
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _stopping = true;

        try
        {
            _queue.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
            // Уже закрыта.
        }

        if (!_worker.Join(TimeSpan.FromSeconds(2)))
            _logger.Warn("Поток выполнения планов не завершился за 2 с");

        _queue.Dispose();
    }
}
