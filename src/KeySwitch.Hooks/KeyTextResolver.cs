using System.Text;
using KeySwitch.Core.Layout;

namespace KeySwitch.Hooks;

/// <summary>
/// Переводит физическое нажатие в символ по раскладке окна, в котором сейчас фокус.
/// </summary>
/// <remarks>
/// Вызывать только из рабочего потока: внутри есть обращения к Win32 (GetGUIThreadInfo,
/// GetKeyboardLayout, ToUnicodeEx). В колбэке низкоуровневого хука это делать нельзя —
/// задержка колбэка останавливает ввод во всей системе.
/// </remarks>
public sealed class KeyTextResolver
{
    /// <summary>Последняя использованная раскладка (для журнала и повторного использования).</summary>
    public string LastLayoutId { get; private set; } = "en-US";

    /// <summary>Идентификатор потока, в котором сейчас фокус ввода.</summary>
    public uint LastFocusThread { get; private set; }

    /// <summary>
    /// Символ нажатия или <c>null</c>, если клавиша не даёт текста
    /// (модификаторы, сочетания с Ctrl/Alt/Win, мёртвые клавиши).
    /// </summary>
    public char? Resolve(
        int virtualKey, int scanCode, bool shift, bool ctrl, bool alt, bool win, bool capsLock,
        out string layoutId)
    {
        // Сочетания с Ctrl/Alt/Win — это команды, а не ввод текста.
        if (ctrl || alt || win || IsModifierKey(virtualKey))
        {
            layoutId = LastLayoutId;
            return null;
        }

        (string resolvedId, IntPtr hkl) = ResolveActiveLayout();
        LastLayoutId = resolvedId;
        layoutId = resolvedId;

        bool effectiveShift = shift;

        LayoutTable? table = LayoutCatalog.TryGet(resolvedId);
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

    /// <summary>
    /// Раскладка потока, владеющего фокусом ввода. Брать раскладку окна переднего плана
    /// нельзя: в WinUI3 (Блокнот, Параметры), Electron и Chrome фокус живёт в отдельном
    /// потоке и даже в отдельном окне, и раскладка у него своя.
    /// </summary>
    private (string LayoutId, IntPtr Hkl) ResolveActiveLayout()
    {
        uint threadId = 0;
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground != IntPtr.Zero)
            threadId = NativeMethods.GetWindowThreadProcessId(foreground, out _);

        // GetWindowThreadProcessId возвращает id ПОТОКА окна, а process id пишет в out-параметр:
        // раскладка живёт по потокам, поэтому нужен именно возвращённый thread id.
        if (threadId != 0)
        {
            var info = new NativeMethods.GUITHREADINFO
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GUITHREADINFO>()
            };

            if (NativeMethods.GetGUIThreadInfo(threadId, ref info) && info.hwndFocus != IntPtr.Zero)
            {
                uint focusThread = NativeMethods.GetWindowThreadProcessId(info.hwndFocus, out _);
                if (focusThread != 0)
                    threadId = focusThread;
            }
        }

        if (threadId == 0)
            threadId = NativeMethods.GetCurrentThreadId();

        LastFocusThread = threadId;

        IntPtr hkl = NativeMethods.GetKeyboardLayout(threadId);
        if (hkl == IntPtr.Zero)
            hkl = NativeMethods.GetKeyboardLayout(0);

        return (LayoutIds.FromHkl(hkl), hkl);
    }

    /// <summary>
    /// Запасной путь для раскладок, которых нет в таблицах (немецкая, французская и т.д.):
    /// ToUnicodeEx с состоянием клавиатуры, собранным из самого события.
    /// </summary>
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

        var buffer = new StringBuilder(8);
        int count = NativeMethods.ToUnicodeEx(
            (uint)virtualKey, (uint)scanCode, keyState, buffer, buffer.Capacity, NoStateChange, hkl);

        // count == -1 — это dead key (символа пока нет), 0 — символа нет вовсе.
        return count > 0 ? buffer[0] : null;
    }

    private static bool IsModifierKey(int virtualKey) => virtualKey switch
    {
        NativeMethods.VK_SHIFT or NativeMethods.VK_CONTROL or NativeMethods.VK_MENU or
        NativeMethods.VK_CAPITAL or NativeMethods.VK_LWIN or NativeMethods.VK_RWIN or
        NativeMethods.VK_LSHIFT or NativeMethods.VK_RSHIFT or NativeMethods.VK_LCONTROL or
        NativeMethods.VK_RCONTROL or NativeMethods.VK_LMENU or NativeMethods.VK_RMENU => true,
        _ => false
    };
}
