using System.ComponentModel;
using System.Runtime.InteropServices;
using KeySwitch.Core.Layout;

namespace KeySwitch.Hooks;

/// <summary>
/// Глобальный низкоуровневый хук клавиатуры (WH_KEYBOARD_LL).
/// Живёт в собственном потоке с циклом сообщений, поэтому не зависит от UI-потока
/// приложения: даже если WPF-окно занято, хук продолжает получать события.
/// Обработчик <see cref="KeyEvent"/> вызывается синхронно на потоке хука.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _started = new(false);
    private readonly NativeMethods.LowLevelKeyboardProc _proc;

    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;
    private volatile bool _running;
    private Exception? _startError;
    private bool _disposed;

    public KeyboardHook()
    {
        // Делегат хранится в поле: если его соберёт GC, произойдёт падение в нативном коде.
        _proc = HookCallback;
    }

    /// <summary>Событие клавиатуры. Подписчик обязан работать быстро и не блокировать поток хука.</summary>
    public event EventHandler<KeyboardHookEventArgs>? KeyEvent;

    /// <summary>Установлен ли хук в данный момент.</summary>
    public bool IsRunning => _running;

    /// <summary>
    /// Последняя ошибка подписчика <see cref="KeyEvent"/>. Исключения не пробрасываются
    /// в нативный код (это уронило бы процесс), но и не теряются молча.
    /// </summary>
    public Exception? LastError { get; private set; }

    /// <summary>Запускает поток хука и устанавливает хук. Повторный вызов ничего не делает.</summary>
    /// <exception cref="InvalidOperationException">Не удалось установить хук.</exception>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is { IsAlive: true })
                return;

            _started.Reset();
            _startError = null;

            var thread = new Thread(HookThreadProc)
            {
                IsBackground = true,
                Name = "KeySwitch.KeyboardHook"
            };
            _thread = thread;
            thread.Start();

            // Ждём установку хука: к моменту возврата Start хук либо работает, либо ошибка.
            if (!_started.Wait(TimeSpan.FromSeconds(5)))
            {
                _thread = null;
                throw new TimeoutException("Поток хука клавиатуры не запустился за 5 секунд.");
            }

            if (_startError is { } error)
            {
                _thread = null;
                throw new InvalidOperationException(
                    "Не удалось установить низкоуровневый хук клавиатуры.", error);
            }
        }
    }

    /// <summary>Снимает хук и останавливает поток. Повторный вызов безопасен.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            Thread? thread = _thread;
            if (thread is null || !thread.IsAlive)
            {
                _thread = null;
                _running = false;
                return;
            }

            // Останавливаем хук под тем же lock, что и Start: иначе параллельный Start
            // успел бы поставить второй хук, пока старый поток ещё не завершился.
            if (thread == Thread.CurrentThread)
            {
                // Остановка из обработчика KeyEvent: поток сам себя не join-ит.
                _thread = null;
                _running = false;
                IntPtr ownHook = Interlocked.Exchange(ref _hook, IntPtr.Zero);
                if (ownHook != IntPtr.Zero)
                    NativeMethods.UnhookWindowsHookEx(ownHook);
                NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                return;
            }

            // Штатный путь: WM_QUIT завершает GetMessage, поток снимает хук в finally.
            for (int attempt = 0; attempt < 3 && thread.IsAlive; attempt++)
            {
                NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                if (thread.Join(200))
                    break;
            }

            if (thread.IsAlive)
            {
                // Аварийный путь: Win32 разрешает снять хук из другого потока.
                IntPtr hook = Interlocked.Exchange(ref _hook, IntPtr.Zero);
                if (hook != IntPtr.Zero)
                    NativeMethods.UnhookWindowsHookEx(hook);

                NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                thread.Join(1000);
            }

            _thread = null;
            _running = false;
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

        try
        {
            _started.Dispose();
        }
        catch (Exception)
        {
            // Поток хука мог не успеть завершиться — утечка одного события некритична.
        }
    }

    // ------------------------------------------------------------ поток хука

    private void HookThreadProc()
    {
        _threadId = NativeMethods.GetCurrentThreadId();

        try
        {
            // Очередь сообщений создаётся лениво; без этого PostThreadMessage(WM_QUIT)
            // из Stop() может потеряться и поток повиснет в GetMessage.
            NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, NativeMethods.PM_NOREMOVE);

            _hook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);

            if (_hook == IntPtr.Zero)
            {
                int code = Marshal.GetLastWin32Error();
                throw new Win32Exception(code, "SetWindowsHookEx(WH_KEYBOARD_LL) вернул NULL.");
            }

            _running = true;
        }
        catch (Exception ex)
        {
            _startError = ex;
            SignalStarted();
            return;
        }

        SignalStarted();

        try
        {
            while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }
        }
        finally
        {
            _running = false;
            IntPtr hook = Interlocked.Exchange(ref _hook, IntPtr.Zero);
            if (hook != IntPtr.Zero)
                NativeMethods.UnhookWindowsHookEx(hook);
        }
    }

    /// <summary>Сигнал о завершении старта. Событие могло быть освобождено Dispose — не падаем.</summary>
    private void SignalStarted()
    {
        try
        {
            _started.Set();
        }
        catch (ObjectDisposedException)
        {
            // Хук уже не нужен: вызывающая сторона освободила объект.
        }
    }

    // ------------------------------------------------------------ обработка

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode < 0)
                return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

            uint message = unchecked((uint)wParam.ToInt64());
            bool isDown = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
            if (!isDown && message is not (NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP))
                return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

            var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            bool injected = (data.flags & NativeMethods.LLKHF_INJECTED) != 0;

            // Свои синтетические события не поднимаем: иначе исправление зациклится.
            // Пропускаем их дальше — они должны дойти до приложения.
            if (injected && data.dwExtraInfo.ToUInt64() == InputSender.InjectionMarker)
                return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

            var args = CreateArgs(data, isDown, injected);

            EventHandler<KeyboardHookEventArgs>? handler = KeyEvent;
            if (handler is not null)
            {
                handler(this, args);
                if (args.Handled)
                    return 1;
            }

            return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }
        catch (Exception ex)
        {
            // Исключение не должно пересекать границу с нативным кодом.
            LastError = ex;
            return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }
    }

    private static KeyboardHookEventArgs CreateArgs(
        NativeMethods.KBDLLHOOKSTRUCT data, bool isKeyDown, bool isInjected)
    {
        bool shift = IsDown(NativeMethods.VK_SHIFT) ||
                     IsDown(NativeMethods.VK_LSHIFT) ||
                     IsDown(NativeMethods.VK_RSHIFT);
        bool ctrl = IsDown(NativeMethods.VK_CONTROL) ||
                    IsDown(NativeMethods.VK_LCONTROL) ||
                    IsDown(NativeMethods.VK_RCONTROL);
        bool alt = IsDown(NativeMethods.VK_MENU) ||
                   IsDown(NativeMethods.VK_LMENU) ||
                   IsDown(NativeMethods.VK_RMENU);
        bool win = IsDown(NativeMethods.VK_LWIN) || IsDown(NativeMethods.VK_RWIN);

        // Младший бит GetAsyncKeyState(VK_CAPITAL) — тумблер CapsLock (наделён глобально).
        bool capsLock = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CAPITAL) & 0x0001) != 0;

        char? character = isKeyDown
            ? ResolveCharacter(data.vkCode, data.scanCode, shift, ctrl, alt, win, capsLock)
            : null;

        return new KeyboardHookEventArgs(
            data.vkCode, data.scanCode, isKeyDown, isInjected, isOurEvent: false,
            shift, ctrl, alt, win, capsLock, character);
    }

    private static bool IsDown(int virtualKey) =>
        (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static bool IsModifierKey(int virtualKey) => virtualKey switch
    {
        NativeMethods.VK_SHIFT or NativeMethods.VK_CONTROL or NativeMethods.VK_MENU or
        NativeMethods.VK_CAPITAL or NativeMethods.VK_LWIN or NativeMethods.VK_RWIN or
        NativeMethods.VK_LSHIFT or NativeMethods.VK_RSHIFT or NativeMethods.VK_LCONTROL or
        NativeMethods.VK_RCONTROL or NativeMethods.VK_LMENU or NativeMethods.VK_RMENU => true,
        _ => false
    };

    /// <summary>
    /// Символ нажатия. Сначала — статическая таблица раскладки (быстро, без Win32),
    /// затем ToUnicodeEx с состоянием клавиатуры, собранным из текущего события
    /// (GetKeyboardState на потоке хука возвращает состояние очереди этого потока,
    /// а не глобальное, поэтому его копия не годится).
    /// </summary>
    private static char? ResolveCharacter(
        int virtualKey, int scanCode, bool shift, bool ctrl, bool alt, bool win, bool capsLock)
    {
        // Сочетания с Ctrl/Alt/Win — это команды, а не ввод текста.
        if (ctrl || alt || win || IsModifierKey(virtualKey))
            return null;

        (string layoutId, IntPtr hkl) = ResolveActiveLayout();
        bool effectiveShift = shift;

        LayoutTable? table = LayoutCatalog.TryGet(layoutId);
        if (table is not null)
        {
            // CapsLock действует только на буквы и инвертирует регистр: upper = caps XOR shift.
            if (table.TryMap(virtualKey, false, out char lower) && char.IsLetter(lower))
                effectiveShift = shift ^ capsLock;

            if (table.TryMap(virtualKey, effectiveShift, out char mapped))
                return mapped;
        }

        return TranslateToUnicode(virtualKey, scanCode, effectiveShift, ctrl, alt, capsLock, hkl);
    }

    private static char? TranslateToUnicode(
        int virtualKey, int scanCode, bool shift, bool ctrl, bool alt, bool capsLock, IntPtr hkl)
    {
        if (hkl == IntPtr.Zero)
            return null;

        var keyState = new byte[256];
        if (shift)
        {
            keyState[NativeMethods.VK_SHIFT] = 0x80;
            keyState[NativeMethods.VK_LSHIFT] = 0x80;
        }

        if (ctrl)
        {
            keyState[NativeMethods.VK_CONTROL] = 0x80;
            keyState[NativeMethods.VK_LCONTROL] = 0x80;
        }

        if (alt)
        {
            keyState[NativeMethods.VK_MENU] = 0x80;
            keyState[NativeMethods.VK_LMENU] = 0x80;
        }

        keyState[NativeMethods.VK_CAPITAL] = (byte)(capsLock ? 0x01 : 0x00);

        // Флаг 0x04 (NoStateChange) не даёт ToUnicodeEx испортить внутреннее состояние
        // dead-key системы, если пользователь нажал мёртвую клавишу.
        const uint NoStateChange = 0x04;

        var buffer = new System.Text.StringBuilder(8);
        int count = NativeMethods.ToUnicodeEx(
            (uint)virtualKey, (uint)scanCode, keyState, buffer, buffer.Capacity, NoStateChange, hkl);

        // count == -1 — это dead key (символа пока нет), 0 — символа нет вовсе.
        return count > 0 ? buffer[0] : null;
    }

    /// <summary>Раскладка активного потока (потока окна переднего плана).</summary>
    private static (string LayoutId, IntPtr Hkl) ResolveActiveLayout()
    {
        // GetWindowThreadProcessId возвращает id ПОТОКА окна, а process id пишет в out-параметр:
        // раскладка живёт по потокам, поэтому нужен именно возвращённый thread id.
        uint threadId = 0;
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground != IntPtr.Zero)
            threadId = NativeMethods.GetWindowThreadProcessId(foreground, out _);

        if (threadId == 0)
            threadId = NativeMethods.GetCurrentThreadId();

        IntPtr hkl = NativeMethods.GetKeyboardLayout(threadId);
        if (hkl == IntPtr.Zero)
            hkl = NativeMethods.GetKeyboardLayout(0);

        return (LayoutIds.FromHkl(hkl), hkl);
    }
}
