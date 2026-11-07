using System.Collections.Concurrent;
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
/// Зачем очередь: пока мы удаляем слово и печатаем исправленное, пользователь может успеть
/// нажать следующую клавишу. Такие нажатия перехватываются хуком (Handled = true), складываются
/// в очередь и воспроизводятся через SendInput сразу после исправления — быстрый набор не теряется.
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

    /// <summary>Ставит в очередь исправление слова (с переключением раскладки).</summary>
    public void ExecutePlan(CorrectionPlan plan, IntPtr targetWindow)
        => ExecuteExclusive(() => ExecutePlanNow(plan, targetWindow));

    /// <summary>
    /// Немедленное исправление на текущем потоке. Вызывать только с потока PlanExecutor
    /// (изнутри <see cref="ExecuteExclusive"/>) — иначе нарушится очередь нажатий.
    /// </summary>
    public void ExecutePlanNow(CorrectionPlan plan, IntPtr targetWindow)
    {
        if (!string.Equals(plan.TargetLayoutId, plan.SourceLayoutId, StringComparison.OrdinalIgnoreCase))
            _layouts.SwitchTo(targetWindow, plan.TargetLayoutId);

        _sender.SendBackspaces(plan.DeleteCount);

        // Терминатор отправляем отдельно: Enter/Tab должны прийти как нажатие VK,
        // а не как Unicode-символ (иначе они не сработают в чужих приложениях).
        _sender.SendText(plan.ReplacementText);
        if (plan.Terminator is { } terminator)
            _sender.SendTerminator(terminator);
    }

    /// <summary>Ставит в очередь отмену исправления.</summary>
    public void ExecuteUndo(UndoPlan plan, IntPtr targetWindow)
        => ExecuteExclusive(() => ExecuteUndoNow(plan, targetWindow));

    /// <summary>Немедленная отмена на текущем потоке (только с потока PlanExecutor).</summary>
    public void ExecuteUndoNow(UndoPlan plan, IntPtr targetWindow)
    {
        if (!string.IsNullOrWhiteSpace(plan.RestoreLayoutId) &&
            !string.Equals(plan.RestoreLayoutId, plan.CurrentLayoutId, StringComparison.OrdinalIgnoreCase))
        {
            _layouts.SwitchTo(targetWindow, plan.RestoreLayoutId);
        }

        _sender.SendBackspaces(plan.DeleteCount);
        _sender.SendText(plan.RestoreText);
        if (plan.Terminator is { } terminator)
            _sender.SendTerminator(terminator);
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
        catch
        {
            // ignore
        }

        if (!_worker.Join(TimeSpan.FromSeconds(2)))
            _logger.Warn("Поток выполнения планов не завершился за 2 с");

        _queue.Dispose();
    }
}
