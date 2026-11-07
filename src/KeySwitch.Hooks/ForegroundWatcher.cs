namespace KeySwitch.Hooks;

/// <summary>
/// Следит за сменой активного окна через <c>SetWinEventHook(EVENT_SYSTEM_FOREGROUND)</c>
/// и сообщает о ней подписчику.
///
/// Зачем: сведения об окне (процесс, раскладка, проверка поля пароля через UI Automation)
/// нужны к моменту первого нажатия, а не после него. Хук обязан отвечать за микросекунды,
/// поэтому прогрев кэша запускается здесь — сразу при смене фокуса, в фоне.
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    private readonly NativeMethods.WinEventDelegate _callback;
    private readonly object _gate = new();

    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;
    private bool _disposed;

    public ForegroundWatcher()
    {
        // Делегат храним в поле: если он соберётся GC, нативный код упадёт.
        _callback = OnWinEvent;
    }

    /// <summary>Сменилось активное окно (передаётся HWND нового окна, может быть нулевым).</summary>
    public event Action<IntPtr>? ForegroundChanged;

    /// <summary>Хук установлен и работает.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _hook != IntPtr.Zero;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _thread is not null)
                return;

            var ready = new ManualResetEventSlim(false);
            _thread = new Thread(() => Run(ready))
            {
                IsBackground = true,
                Name = "KeySwitch.ForegroundWatcher"
            };
            _thread.Start();
            ready.Wait(TimeSpan.FromSeconds(2));
        }
    }

    public void Stop()
    {
        Thread? thread;
        uint threadId;
        lock (_gate)
        {
            thread = _thread;
            threadId = _threadId;
            _thread = null;
            _threadId = 0;
        }

        if (thread is null)
            return;

        if (threadId != 0)
            NativeMethods.PostThreadMessage(threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);

        if (!thread.Join(TimeSpan.FromSeconds(2)))
        {
            // Поток не проснулся — не блокируем вызывающего дольше.
        }
    }

    private void Run(ManualResetEventSlim ready)
    {
        try
        {
            lock (_gate)
            {
                _threadId = NativeMethods.GetCurrentThreadId();
            }

            _hook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _callback,
                0,
                0,
                NativeMethods.WINEVENT_OUTOFCONTEXT);

            ready.Set();

            if (_hook == IntPtr.Zero)
                return;

            // Насос сообщений: без него колбэк OUTOFCONTEXT не вызывается.
            while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_hook != IntPtr.Zero)
                {
                    NativeMethods.UnhookWinEvent(_hook);
                    _hook = IntPtr.Zero;
                }
            }

            ready.Set();
        }
    }

    private void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (eventType != NativeMethods.EVENT_SYSTEM_FOREGROUND)
            return;

        // Колбэк приходит с потока этого же класса, но подписчик может бросить —
        // исключение не должно ломать насос сообщений.
        try
        {
            ForegroundChanged?.Invoke(hwnd);
        }
        catch
        {
            // Подписчик обязан логировать свои ошибки сам; здесь только защита насоса.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        Stop();
    }
}
