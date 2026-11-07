using System.Collections.Concurrent;
using System.Threading;
using KeySwitch.Core.Correction;
using KeySwitch.Core.Engine;
using KeySwitch.Core.Input;
using KeySwitch.Core.Layout;
using KeySwitch.Data;
using KeySwitch.Hooks;

// UseWindowsForms добавляет глобальные using System.Windows.Forms — уточняем, чей Timer нужен.
using Timer = System.Threading.Timer;

namespace KeySwitch.App.Services;

/// <summary>Что за работа пришла из хука.</summary>
internal enum KeyWorkKind
{
    /// <summary>Обычное нажатие или отпускание клавиши.</summary>
    Key,

    /// <summary>Нажата горячая клавиша паузы.</summary>
    PauseHotkey,

    /// <summary>Нажата горячая клавиша ручной конвертации.</summary>
    ManualHotkey
}

/// <summary>
/// Копия нажатия для рабочего потока. Хук заполняет её и сразу возвращается,
/// поэтому здесь только значения — никаких ссылок на объекты хука.
/// </summary>
internal readonly record struct KeyWorkItem(
    KeyWorkKind Kind,
    int VirtualKey,
    int ScanCode,
    char? Character,
    bool Shift,
    bool Ctrl,
    bool Alt,
    bool Win,
    bool CapsLock,
    bool IsKeyDown,
    bool Swallowed,
    string? LayoutId = null)
{
    public static KeyWorkItem Hotkey(KeyWorkKind kind) =>
        new(kind, 0, 0, null, false, false, false, false, false, true, false);
}

/// <summary>
/// Сердце приложения: приём нажатий из хука и их обработка в рабочем потоке.
///
/// Правила, которые здесь зашиты:
/// * колбэк хука только копирует нажатие в очередь и отвечает — вся тяжёлая работа
///   (окно, UI Automation, детектор, SendInput, смена раскладки) идёт в рабочем потоке;
/// * план исправления — единственное основание перехватить нажатие (Handled = true), и только
///   для Enter/Tab, когда в буфере есть слово: обычный терминатор (пробел, «.», «,») система
///   уже доставила в окно, поэтому его перехват сломал бы набор;
/// * пока выполняется план, нажатия пользователя складываются в очередь и воспроизводятся после;
/// * двойной Shift (два «тапа» меньше 400 мс без других клавиш) отменяет последнее исправление;
/// * Ctrl+Alt+P — пауза автопереключения (решение зафиксировано в docs/notes/app.md).
/// </summary>
public sealed class AutoSwitchService : IDisposable
{
    /// <summary>
    /// Обрабатывать ли ввод, сгенерированный другой программой (LLKHF_INJECTED).
    /// По умолчанию нет; переменная окружения нужна автотестам, которые печатают
    /// через keybd_event/SendInput из стенда, а не с физической клавиатуры.
    /// </summary>
    internal static bool AllowInjectedInput { get; } =
        Environment.GetEnvironmentVariable("KEYSWITCH_ALLOW_INJECTED") == "1";

    // Низкоуровневый хук отдаёт левый/правый Shift как отдельные клавиши (0xA0/0xA1),
    // KeyClassifier.VkShift (0x10) в WH_KEYBOARD_LL не приходит.
    private const int VkLShift = 0xA0;
    private const int VkRShift = 0xA1;
    private const int VkC = 0x43;
    private const int VkV = 0x56;

    private const int ShiftTapMaxMs = 400;
    private const int ShiftCancelWindowMs = 400;
    private const int SelectionCopyTimeoutMs = 500;
    private const int PasteDelayMs = 90;

    private const string PauseHotkeySpec = "Ctrl+Alt+P";

    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly CorrectionEngine _engine;
    private readonly PlanExecutor _executor;
    private readonly KeyboardHook _hook;
    private readonly InputSender _sender;
    private readonly LayoutSwitcher _layouts;
    private readonly ClipboardHelper _clipboard;
    private readonly ForegroundTracker _tracker;

    /// <summary>Разбор нажатия в символ и раскладку — только из рабочего потока.</summary>
    private readonly KeyTextResolver _text = new();
    private readonly ExceptionStore _exceptions;
    private readonly SoundService? _sound;
    private readonly AppLogger _logger;

    private readonly Hotkey? _pauseHotkey = Hotkey.TryParse(PauseHotkeySpec);
    private readonly Timer _singleShiftTimer;
    private Hotkey? _manualHotkey;

    private long _shiftDownAt;
    private bool _shiftHadOtherKey;
    private bool _keySinceShiftTap;
    private long _lastShiftTapAt = long.MinValue / 2;
    private IntPtr _singleShiftWindow;
    private int _singleShiftPending;

    private bool _passwordLike;
    private volatile bool _started;
    private volatile bool _hasPendingWord;
    private bool _disposed;

    /// <summary>
    /// Окно, о котором уже сообщили, что оно исключено. Нужно, чтобы причина «здесь не работаем»
    /// попадала в журнал один раз на окно, а не на каждое нажатие.
    /// </summary>
    private IntPtr _skipLoggedFor;

    // Очередь нажатий: хук кладёт — рабочий поток разбирает. Порядок сохраняется,
    // поэтому буфер слова всегда видит клавиши в том же порядке, в каком их нажали.
    private readonly BlockingCollection<KeyWorkItem> _keys = new(new ConcurrentQueue<KeyWorkItem>());
    private readonly Thread _worker;

    public AutoSwitchService(
        AppSettings settings,
        SettingsStore store,
        CorrectionEngine engine,
        PlanExecutor executor,
        KeyboardHook hook,
        InputSender sender,
        LayoutSwitcher layouts,
        ClipboardHelper clipboard,
        ForegroundTracker tracker,
        ExceptionStore exceptions,
        SoundService? sound,
        AppLogger logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _hook = hook ?? throw new ArgumentNullException(nameof(hook));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _layouts = layouts ?? throw new ArgumentNullException(nameof(layouts));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _exceptions = exceptions ?? throw new ArgumentNullException(nameof(exceptions));
        _sound = sound;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _manualHotkey = Hotkey.TryParse(settings.ManualConvertHotkey);
        _singleShiftTimer = new Timer(_ => OnSingleShiftTimeout(), null, Timeout.Infinite, Timeout.Infinite);

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "KeySwitch.KeyProcessor"
        };
        _worker.Start();

        ApplyDetectionSettings();
    }

    /// <summary>Автопереключение включено пользователем.</summary>
    public bool Enabled => _settings.Enabled;

    /// <summary>Хук запущен (в SafeMode не запускается).</summary>
    public bool IsStarted => _started;

    /// <summary>Включение/выключение из трея или горячей клавишей.</summary>
    public event EventHandler? StateChanged;

    public void Start()
    {
        if (_disposed || _started)
            return;

        _hook.KeyEvent += OnKeyEvent;
        try
        {
            _hook.Start();
            _started = true;
            _logger.Info("Автопереключение запущено.");
        }
        catch (Exception ex)
        {
            _hook.KeyEvent -= OnKeyEvent;
            _logger.Error("Не удалось запустить хук клавиатуры", ex);
            throw;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        if (!_started)
            return;

        _hook.KeyEvent -= OnKeyEvent;
        _hook.Stop();
        _started = false;
        _logger.Info("Автопереключение остановлено.");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Перечитать настройки после «Сохранить» в окне настроек.</summary>
    public void Reconfigure()
    {
        _manualHotkey = Hotkey.TryParse(_settings.ManualConvertHotkey);
        ApplyDetectionSettings();

        if (!_settings.Enabled)
            ResetState();

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Включить/выключить автопереключение (трей и Ctrl+Alt+P). Сохраняет настройки.</summary>
    public void ToggleEnabled()
    {
        _settings.Enabled = !_settings.Enabled;
        ResetState();
        try
        {
            _store.Save();
        }
        catch (Exception ex)
        {
            _logger.Error("Не удалось сохранить настройки после переключения", ex);
        }

        _logger.Info(_settings.Enabled ? "Автопереключение включено." : "Автопереключение выключено.");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Ручная конвертация выделения (или последнего слова) — для трея/горячей клавиши.</summary>
    public void RequestManualConvert()
    {
        if (_executor.IsBusy)
            return;

        IntPtr window = _tracker.Get().Handle;
        string layoutId = ResolveLayoutId();
        _executor.ExecuteExclusive(() => ConvertSelectionCore(window, layoutId));
    }

    // ------------------------------------------------------------ хук

    /// <summary>
    /// Колбэк хука. Обязан вернуться за микросекунды: только фильтры, проверка горячих
    /// клавиш (несколько сравнений) и постановка нажатия в очередь.
    /// </summary>
    private void OnKeyEvent(object? sender, KeyboardHookEventArgs e)
    {
        if (e.IsOurEvent)
            return;

        // Ввод, сгенерированный другой программой, по умолчанию игнорируем: так мы не
        // реагируем на чужую автоматизацию и на собственные события. Для автотестов
        // (keybd_event/SendInput из стенда) фильтр снимается переменной окружения.
        if (e.IsInjected && !AllowInjectedInput)
            return;

        try
        {
            // Пока идёт исправление, нажатия не отдаём приложению: соберём и воспроизведём
            // после, иначе быстрый набор попадёт между Backspace и новым текстом.
            if (_executor.IsBusy)
            {
                if (_executor.TryBlockKey(ToReplayItem(e)))
                    e.Handled = true;
                return;
            }

            if (e.IsKeyDown)
            {
                if (_pauseHotkey is not null &&
                    _pauseHotkey.Matches(e.VirtualKey, e.Ctrl, e.Alt, e.Shift, e.Win))
                {
                    e.Handled = true;
                    Enqueue(KeyWorkItem.Hotkey(KeyWorkKind.PauseHotkey));
                    return;
                }

                if (_manualHotkey is not null &&
                    _manualHotkey.Matches(e.VirtualKey, e.Ctrl, e.Alt, e.Shift, e.Win))
                {
                    e.Handled = true;
                    Enqueue(KeyWorkItem.Hotkey(KeyWorkKind.ManualHotkey));
                    return;
                }

                // Enter и Tab перехватываем только когда в буфере есть слово: иначе они
                // сработают в чужом окне раньше, чем мы успеем исправить предыдущее.
                if (_hasPendingWord &&
                    e.VirtualKey is KeyClassifier.VkReturn or KeyClassifier.VkTab)
                {
                    e.Handled = true;
                    Enqueue(FromEvent(e, swallowed: true));
                    return;
                }
            }

            Enqueue(FromEvent(e, swallowed: false));
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка постановки нажатия в очередь", ex);
        }
    }

    private void Enqueue(KeyWorkItem item)
    {
        try
        {
            _keys.Add(item);
        }
        catch (InvalidOperationException)
        {
            // Очередь закрыта — приложение завершается.
        }
    }

    private void WorkerLoop()
    {
        try
        {
            foreach (var item in _keys.GetConsumingEnumerable())
            {
                try
                {
                    ProcessKey(item);
                }
                catch (Exception ex)
                {
                    _logger.Error("Ошибка обработки нажатия", ex);
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Нормальное завершение.
        }
        catch (Exception ex)
        {
            _logger.Error("Поток обработки нажатий упал", ex);
        }
    }

    private void ProcessKey(KeyWorkItem item)
    {
        switch (item.Kind)
        {
            case KeyWorkKind.PauseHotkey:
                ToggleEnabled();
                return;

            case KeyWorkKind.ManualHotkey:
                RequestManualConvert();
                return;
        }

        if (!_started || _disposed)
            return;

        // Символ и раскладка разрешаются здесь, в рабочем потоке: в колбэке хука на это
        // нет времени (GetGUIThreadInfo/GetKeyboardLayout/ToUnicodeEx — вызовы Win32).
        if (item.Kind == KeyWorkKind.Key && item.IsKeyDown)
        {
            char? resolved = _text.Resolve(
                item.VirtualKey, item.ScanCode, item.Shift, item.Ctrl, item.Alt, item.Win,
                item.CapsLock, out string layoutId);
            item = item with { Character = resolved, LayoutId = layoutId };
        }

        if (item.IsKeyDown)
            OnKeyDownCore(item);
        else
            OnKeyUpCore(item);
    }

    private void OnKeyDownCore(KeyWorkItem e)
    {
        int vk = e.VirtualKey;

        if (vk is VkLShift or VkRShift)
        {
            _shiftDownAt = Environment.TickCount64;
            _shiftHadOtherKey = false;
            return;
        }

        // Любая другая клавиша между нажатиями Shift снимает «тап».
        _shiftHadOtherKey = true;
        _keySinceShiftTap = true;

        if (!_settings.Enabled)
            return;

        if (vk == KeyClassifier.VkPause && _settings.PauseHotkeyEnabled)
        {
            HandlePause();
            return;
        }

        var info = _tracker.Get();
        var mode = ResolveMode(info);
        if (mode == ExceptionMode.Disabled || IsGameOrProtected(info))
        {
            LogWindowSkip(info, mode);
            ResetState();
            return;
        }

        // AutoSwitch выключен или процесс в ManualOnly: буфер ведём, но планы не исполняем —
        // это нужно, чтобы работала ручная конвертация последнего слова.
        bool auto = _settings.AutoSwitch && mode == ExceptionMode.Full;

        var role = KeyClassifier.Classify(vk, e.Ctrl, e.Alt, e.Win, e.Character);
        switch (role)
        {
            case KeyRole.Ignore:
                return;

            case KeyRole.Reset:
                ResetState();
                _engine.ClearUndo();
                return;

            case KeyRole.Backspace:
                if (_engine.Buffer.IsEmpty)
                    _engine.ClearUndo();
                _engine.Backspace();
                _hasPendingWord = !_engine.Buffer.IsEmpty;
                return;

            case KeyRole.WordChar:
                HandleWordChar(e, info, auto);
                return;

            case KeyRole.Terminator:
                HandleTerminator(e, info, auto);
                return;
        }
    }

    private void OnKeyUpCore(KeyWorkItem e)
    {
        if (e.VirtualKey is not (VkLShift or VkRShift))
            return;

        bool tap = !_shiftHadOtherKey && Environment.TickCount64 - _shiftDownAt <= ShiftTapMaxMs;
        _shiftHadOtherKey = false;
        if (!tap || !_settings.Enabled)
            return;

        long now = Environment.TickCount64;

        bool doubleTap = _settings.DoubleShiftUndo &&
                         now - _lastShiftTapAt <= ShiftCancelWindowMs &&
                         !_keySinceShiftTap;

        _lastShiftTapAt = now;
        _keySinceShiftTap = false;

        if (doubleTap)
        {
            // Второй «тап» без других клавиш в пределах окна — отмена последнего исправления.
            _lastShiftTapAt = long.MinValue / 2;
            CancelSingleShiftSwitch();
            TryUndo("двойной Shift");
            return;
        }

        if (!_settings.SingleShiftSwitch)
            return;

        _singleShiftWindow = _tracker.Get().Handle;
        if (_settings.DoubleShiftUndo)
        {
            // Ждём, не будет ли второго тапа: иначе одиночный Shift и двойной конфликтуют.
            Interlocked.Exchange(ref _singleShiftPending, 1);
            _singleShiftTimer.Change(ShiftCancelWindowMs, Timeout.Infinite);
        }
        else
        {
            SwitchLayoutNow(_singleShiftWindow);
        }
    }

    private void HandleWordChar(KeyWorkItem e, ForegroundWindowInfo info, bool auto)
    {
        if (e.Character is not { } ch)
            return;

        // Поле пароля — не трогаем вообще: ни буфера, ни переключений.
        if (_tracker.IsPasswordField(_settings.PasswordProtectionEnabled))
        {
            if (_skipLoggedFor != info.Handle)
            {
                _skipLoggedFor = info.Handle;
                _logger.Info($"Поле пароля: proc={info.ProcessName} — ввод не обрабатывается.");
            }

            _passwordLike = true;
            _engine.Reset();
            _hasPendingWord = false;
            return;
        }

        if (_passwordLike)
            return;

        string layoutId = e.LayoutId ?? ResolveLayoutId(info);
        var stroke = new KeyStroke(e.VirtualKey, e.Shift, e.CapsLock, e.ScanCode);
        _engine.AppendChar(stroke, ch, layoutId);
        _hasPendingWord = !_engine.Buffer.IsEmpty;

        // Эвристика «похоже на пароль»: строчные + заглавные + цифры/знаки и длина > 6.
        // Такой ввод не исправляем и раскладку не трогаем — оставляем как набрано.
        if (_settings.PasswordProtectionEnabled && LooksLikePassword(_engine.Buffer.Text))
        {
            _passwordLike = true;
            _engine.Reset();
            _engine.ClearUndo();
            _hasPendingWord = false;
            _logger.Debug("Похоже на пароль — автопереключение для слова отключено.");
            return;
        }

        if (!auto || _engine.Buffer.IsEmpty)
            return;

        var plan = _engine.TryEarlySwitch();
        if (plan is null)
            return;

        // Клавишу перехватить уже нельзя (хук вернулся раньше), поэтому последний символ
        // реально стоит в окне — DeleteCount движка его учитывает как есть.
        _hasPendingWord = false;
        _executor.ExecutePlan(plan, info.Handle, info.ProcessName);
        PlayFixSound();
    }

    private void HandleTerminator(KeyWorkItem e, ForegroundWindowInfo info, bool auto)
    {
        char? terminator = e.Character ?? DefaultTerminator(e.VirtualKey);
        bool swallowed = e.Swallowed;

        // Enter/Tab перехватываем: иначе они сработают в чужом окне до того, как мы исправим слово.
        // Терминатор известен всегда: если клавишу проглотили — её напечатает executor, если она уже
        // дошла до окна — движок добавил её к DeleteCount и вернёт вместе с исправленным словом.
        var result = _engine.CompleteWord(terminator, terminatorAlreadyTyped: !swallowed);

        _passwordLike = false;
        _hasPendingWord = false;

        LogWordDecision(info, result, auto);

        if (result.Plan is not { } plan || !auto)
        {
            // Нажатие перехвачено, но исправлять нечего — возвращаем Enter/Tab в окно,
            // иначе он потеряется.
            if (swallowed && terminator is { } typed)
                _sender.SendTerminator(typed);
            return;
        }

        _executor.ExecutePlan(plan, info.Handle, info.ProcessName);
        PlayFixSound();
    }

    /// <summary>
    /// Пишет в журнал, почему окно целиком исключено из автопереключения. Без этой строки
    /// «ничего не происходит» выглядит одинаково для всех причин сразу.
    /// </summary>
    private void LogWindowSkip(ForegroundWindowInfo info, ExceptionMode mode)
    {
        if (_skipLoggedFor == info.Handle)
            return;

        _skipLoggedFor = info.Handle;
        bool game = IsGameOrProtected(info);
        _logger.Info(
            $"Окно исключено: proc={info.ProcessName} режим={mode} fullscreen={info.IsFullscreen} " +
            $"игра_или_защищено={game} elevated={info.IsElevated}");
    }

    /// <summary>
    /// Решение по завершённому слову. Текст набора в журнал не попадает никогда — только длина,
    /// раскладка и причина решения (безопасный режим, п.13 ТЗ).
    /// </summary>
    private void LogWordDecision(ForegroundWindowInfo info, EngineResult result, bool auto)
    {
        if (result.Word is not { } word)
            return;

        var plan = result.Plan;
        _logger.Info(
            $"Слово: proc={info.ProcessName} символов={word.Text.Length} раскладка={word.LayoutId} " +
            $"авто={(auto ? "да" : "нет")} решение={(plan is null ? "оставить" : "исправить")} " +
            $"причина={result.Note ?? "-"}" +
            (plan is null
                ? string.Empty
                : $" уверенность={plan.Confidence:0.00} цель={plan.TargetLayoutId}"));
    }

    private void HandlePause()
    {
        // Pause/Break: сначала отмена последнего исправления, если отменять нечего —
        // ручная конвертация последнего слова.
        if (_engine.CanUndo)
        {
            TryUndo("Pause/Break");
            return;
        }

        var result = _engine.ConvertManually();
        if (result.Plan is not { } plan)
        {
            _logger.Debug("Pause/Break: конвертировать нечего.");
            return;
        }

        var info = _tracker.Get();
        _executor.ExecutePlan(plan, info.Handle, info.ProcessName);
        PlayFixSound();
    }

    private bool TryUndo(string reason)
    {
        if (!_engine.TryUndo(out var plan) || plan is null)
        {
            _logger.Debug($"{reason}: отменять нечего.");
            return false;
        }

        _executor.ExecuteUndo(plan, _tracker.Get().Handle);
        PlayFixSound();
        _logger.Debug($"Отмена исправления ({reason}).");
        return true;
    }

    // ------------------------------------------------------------ выделение

    private void ConvertSelectionCore(IntPtr window, string layoutId)
    {
        string? snapshot = _clipboard.CaptureAll();

        try
        {
            // Очищаем буфер, чтобы отличить «выделения нет» от «выделение совпало с буфером».
            _clipboard.SetText(string.Empty);
            SendCtrlC();

            string? text = WaitForClipboardText(SelectionCopyTimeoutMs);
            if (string.IsNullOrWhiteSpace(text))
            {
                // Выделения нет — конвертируем последнее слово (Pause-путь).
                _clipboard.RestoreAll(snapshot);
                var manual = _engine.ConvertManually();
                if (manual.Plan is { } plan)
                {
                    _executor.ExecutePlanNow(plan, window);
                    PlayFixSound();
                }

                return;
            }

            var selectionPlan = _engine.ConvertSelection(text, layoutId);
            if (selectionPlan is null)
            {
                _clipboard.RestoreAll(snapshot);
                _logger.Debug("Выделение: конвертация не требуется.");
                return;
            }

            _layouts.Ensure(window, selectionPlan.TargetLayoutId);
            _clipboard.SetText(selectionPlan.ReplacementText);
            SendCtrlV();
            Thread.Sleep(PasteDelayMs);

            if (snapshot is null)
                _clipboard.SetText(string.Empty);
            else
                _clipboard.RestoreAll(snapshot);

            PlayFixSound();
            _logger.Debug("Выделение сконвертировано.");
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка конвертации выделения", ex);
            _clipboard.RestoreAll(snapshot);
        }
        finally
        {
            // Undo для конвертации выделения не поддерживается: выделение уже заменено.
            _engine.ClearUndo();
        }
    }

    private string? WaitForClipboardText(int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            Thread.Sleep(10);
            string? text = _clipboard.GetText();
            if (!string.IsNullOrEmpty(text))
                return text;
        }

        return null;
    }

    private void SendCtrlC() => SendCtrlKey(VkC);

    private void SendCtrlV() => SendCtrlKey(VkV);

    private void SendCtrlKey(int virtualKey)
    {
        _sender.SendKeyStroke(KeyClassifier.VkControl, false, keyUp: false);
        _sender.SendKeyStroke(virtualKey, false, keyUp: false);
        _sender.SendKeyStroke(virtualKey, false, keyUp: true);
        _sender.SendKeyStroke(KeyClassifier.VkControl, false, keyUp: true);
    }

    // ------------------------------------------------------------ вспомогательное

    private void ApplyDetectionSettings()
    {
        var detection = _engine.Settings;
        detection.ConfidenceThreshold = _settings.ConfidenceThreshold;
        detection.NoDictionaryThresholdMultiplier = _settings.NoDictionaryThresholdMultiplier;
        detection.EarlySwitchEnabled = _settings.EarlySwitchEnabled;
        detection.EarlySwitchMinLength = _settings.EarlySwitchMinLength;
        detection.EarlySwitchThreshold = _settings.EarlySwitchThreshold;
    }

    private ExceptionMode ResolveMode(ForegroundWindowInfo info)
    {
        try
        {
            return _exceptions.GetMode(info.ProcessName, info.ProcessPath);
        }
        catch (Exception ex)
        {
            _logger.Debug($"ExceptionStore.GetMode: {ex.Message}");
            return ExceptionMode.Full;
        }
    }

    private bool IsGameOrProtected(ForegroundWindowInfo info)
    {
        if (_settings.GameDetectionEnabled)
        {
            // Полноэкранный браузер — это F11, полноэкранное видео или PWA, а не игра:
            // правило живёт в WindowClassification, потому что покрыто тестами.
            if (WindowClassification.IsFullscreenGame(info))
                return true;

            if (ExceptionStore.IsGamesFolder(info.ProcessPath))
                return true;
        }

        // Окно с повышенными правами при обычном запуске: UIPI не даст отправить в него ввод,
        // а исправление «вслепую» только испортит текст.
        return info.IsElevated && !ElevationHelper.IsProcessElevated;
    }

    private string ResolveLayoutId(ForegroundWindowInfo info)
    {
        if (LayoutCatalog.TryGet(info.LayoutId) is not null)
            return info.LayoutId;

        try
        {
            string current = _layouts.GetLayoutId(info.Handle);
            if (LayoutCatalog.TryGet(current) is not null)
                return current;
        }
        catch (Exception ex)
        {
            _logger.Debug($"GetLayoutId: {ex.Message}");
        }

        return LayoutCatalog.EnUsId;
    }

    private string ResolveLayoutId() => ResolveLayoutId(_tracker.Get());

    private void SwitchLayoutNow(IntPtr window)
    {
        string current = ResolveLayoutId(_tracker.Get());
        string target = string.Equals(current, LayoutCatalog.EnUsId, StringComparison.OrdinalIgnoreCase)
            ? LayoutCatalog.RuRuId
            : LayoutCatalog.EnUsId;

        try
        {
            if (_layouts.SwitchTo(window, target))
                PlaySwitchSound();
        }
        catch (Exception ex)
        {
            _logger.Error("Не удалось переключить раскладку", ex);
        }
    }

    private void CancelSingleShiftSwitch()
    {
        if (Interlocked.Exchange(ref _singleShiftPending, 0) == 0)
            return;

        try
        {
            _singleShiftTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // Приложение завершается.
        }
    }

    private void OnSingleShiftTimeout()
    {
        if (_disposed || Interlocked.Exchange(ref _singleShiftPending, 0) == 0)
            return;

        if (!_settings.Enabled || !_settings.SingleShiftSwitch)
            return;

        SwitchLayoutNow(_singleShiftWindow);
    }

    private void ResetState()
    {
        _engine.Reset();
        _passwordLike = false;
        _hasPendingWord = false;
    }

    private static bool LooksLikePassword(string text)
    {
        if (text.Length <= 6)
            return false;

        bool lower = false, upper = false, digitOrSymbol = false;
        foreach (char ch in text)
        {
            if (char.IsLower(ch)) lower = true;
            else if (char.IsUpper(ch)) upper = true;
            else if (char.IsDigit(ch) || (!char.IsLetter(ch) && !char.IsWhiteSpace(ch))) digitOrSymbol = true;
        }

        return lower && upper && digitOrSymbol;
    }

    private static char? DefaultTerminator(int virtualKey) => virtualKey switch
    {
        KeyClassifier.VkSpace => ' ',
        KeyClassifier.VkReturn => '\r',
        KeyClassifier.VkTab => '\t',
        _ => null
    };

    private static KeyWorkItem FromEvent(KeyboardHookEventArgs e, bool swallowed) => new(
        KeyWorkKind.Key,
        e.VirtualKey,
        e.ScanCode,
        e.Character,
        e.Shift,
        e.Ctrl,
        e.Alt,
        e.Win,
        e.CapsLock,
        e.IsKeyDown,
        swallowed);

    private static KeyReplayItem ToReplayItem(KeyboardHookEventArgs e) => new(
        e.VirtualKey,
        e.ScanCode,
        e.Shift,
        e.Ctrl,
        e.Alt,
        e.Win,
        e.CapsLock,
        e.IsKeyDown,
        e.Character);

    private void PlayFixSound() => PlaySound(_settings.FixSoundPath);

    private void PlaySwitchSound() => PlaySound(_settings.SwitchSoundPath);

    private void PlaySound(string? path)
    {
        if (_sound is null || !_settings.SoundsEnabled || string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            _sound.Play(path, _settings.SoundVolume);
        }
        catch (Exception ex)
        {
            _logger.Debug($"Звук: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();

        try
        {
            _singleShiftTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
        }

        _singleShiftTimer.Dispose();

        try
        {
            _keys.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
            // Очередь уже закрыта.
        }

        if (!_worker.Join(TimeSpan.FromSeconds(2)))
            _logger.Warn("Поток обработки нажатий не завершился за 2 с");

        _keys.Dispose();
    }
}
