using System.Diagnostics;

namespace KeySwitch.Hooks;

/// <summary>
/// Переключение раскладки конкретного окна. Ключевой факт Win32: раскладка хранится
/// не глобально, а на потоке (у окна — раскладка его потока), поэтому «сменить язык»
/// означает либо попросить окно сменить её (WM_INPUTLANGCHANGEREQUEST), либо
/// активировать её на нужном потоке (AttachThreadInput + ActivateKeyboardLayout).
/// </summary>
public sealed class LayoutSwitcher
{
    /// <summary>Сколько ждать применения запроса смены раскладки.</summary>
    private const int SwitchTimeoutMs = 60;

    private const int PollIntervalMs = 5;

    /// <summary>Максимум попыток активации «на потоке» после неудачного запроса.</summary>
    private const int ActivateAttempts = 2;

    /// <summary>Идентификатор раскладки потока указанного окна (например "ru-RU" или "0x0419").</summary>
    public string GetLayoutId(IntPtr window)
    {
        uint threadId = ThreadOf(window);
        IntPtr hkl = NativeMethods.GetKeyboardLayout(threadId);
        if (hkl == IntPtr.Zero)
            hkl = NativeMethods.GetKeyboardLayout(0);

        return LayoutIds.FromHkl(hkl);
    }

    /// <summary>
    /// Переключает раскладку окна на <paramref name="layoutId"/> и проверяет результат.
    /// Возвращает true, если раскладка действительно сменилась.
    /// </summary>
    public bool SwitchTo(IntPtr window, string layoutId)
    {
        if (!LayoutIds.TryParse(layoutId, out int langId))
            return false;

        uint threadId = ThreadOf(window);
        IntPtr target = FindHkl(langId);
        if (target == IntPtr.Zero)
            return false;

        if (LayoutIds.LangIdFromHkl(NativeMethods.GetKeyboardLayout(threadId)) == langId)
            return true;

        IntPtr requestWindow = ResolveRequestWindow(window);

        // Основной путь: попросить окно сменить раскладку. Сообщение обрабатывает
        // сам поток окна, поэтому смена происходит именно там, где нужно.
        if (requestWindow != IntPtr.Zero)
            NativeMethods.PostMessage(
                requestWindow, NativeMethods.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, target);

        if (WaitForLayout(threadId, langId))
            return true;

        // Запасной путь: активировать раскладку прямо на потоке окна. Для этого
        // нужно присоединить очередь ввода, иначе ActivateKeyboardLayout подействует
        // только на текущий поток.
        for (int attempt = 0; attempt < ActivateAttempts; attempt++)
        {
            bool attached = threadId != 0 && threadId != NativeMethods.GetCurrentThreadId() &&
                            NativeMethods.AttachThreadInput(
                                NativeMethods.GetCurrentThreadId(), threadId, true);
            try
            {
                NativeMethods.ActivateKeyboardLayout(target, 0);
            }
            finally
            {
                if (attached)
                    NativeMethods.AttachThreadInput(
                        NativeMethods.GetCurrentThreadId(), threadId, false);
            }

            if (WaitForLayout(threadId, langId))
                return true;
        }

        return LayoutIds.LangIdFromHkl(NativeMethods.GetKeyboardLayout(threadId)) == langId;
    }

    /// <summary>
    /// Переключает раскладку только если она ещё не нужная. Основная точка входа
    /// для конвейера исправления: лишняя смена языка — это лишний Shift пользователю.
    /// </summary>
    public bool Ensure(IntPtr window, string layoutId)
    {
        if (!LayoutIds.TryParse(layoutId, out int langId))
            return false;

        uint threadId = ThreadOf(window);
        if (LayoutIds.LangIdFromHkl(NativeMethods.GetKeyboardLayout(threadId)) == langId)
            return true;

        return SwitchTo(window, layoutId);
    }

    // ------------------------------------------------------------ внутреннее

    private static uint ThreadOf(IntPtr window)
    {
        if (window != IntPtr.Zero)
        {
            uint threadId = NativeMethods.GetWindowThreadProcessId(window, out _);
            if (threadId != 0)
                return threadId;
        }

        return NativeMethods.GetCurrentThreadId();
    }

    /// <summary>
    /// Окно, которому отправляется WM_INPUTLANGCHANGEREQUEST. Для консольных окон
    /// адресат — консольное окно процесса, иначе сообщение уйдёт в никуда.
    /// </summary>
    private static IntPtr ResolveRequestWindow(IntPtr window)
    {
        if (window == IntPtr.Zero)
            return IntPtr.Zero;

        string windowClass = NativeMethods.GetWindowClass(window);
        if (windowClass is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS")
        {
            IntPtr console = NativeMethods.GetConsoleWindow();
            if (console != IntPtr.Zero && console != window)
                return console;
        }

        return window;
    }

    /// <summary>HKL для языка: сначала среди уже загруженных, затем LoadKeyboardLayout.</summary>
    private static IntPtr FindHkl(int langId)
    {
        int count = NativeMethods.GetKeyboardLayoutList(0, null);
        if (count > 0)
        {
            var list = new IntPtr[count];
            int filled = NativeMethods.GetKeyboardLayoutList(count, list);
            for (int i = 0; i < filled; i++)
            {
                if (LayoutIds.LangIdFromHkl(list[i]) == langId)
                    return list[i];
            }
        }

        return NativeMethods.LoadKeyboardLayout(LayoutIds.ToKlid(langId), 0);
    }

    /// <summary>Ожидание смены раскладки у потока. Опрос короткий: конвейер ждёт синхронно.</summary>
    private static bool WaitForLayout(uint threadId, int langId)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < SwitchTimeoutMs)
        {
            if (LayoutIds.LangIdFromHkl(NativeMethods.GetKeyboardLayout(threadId)) == langId)
                return true;

            Thread.Sleep(PollIntervalMs);
        }

        return LayoutIds.LangIdFromHkl(NativeMethods.GetKeyboardLayout(threadId)) == langId;
    }
}
