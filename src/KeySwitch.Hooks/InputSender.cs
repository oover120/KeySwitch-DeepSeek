using System.Runtime.InteropServices;

namespace KeySwitch.Hooks;

/// <summary>
/// Отправка синтетического ввода через SendInput. Все события помечаются
/// <see cref="InjectionMarker"/> в dwExtraInfo, чтобы собственный хук их не обрабатывал.
/// Методы потокобезопасны и не требуют STA.
/// </summary>
public sealed class InputSender : IDisposable
{
    /// <summary>
    /// Маркер «своё» событие в dwExtraInfo. Формат: 0x4B53_0001 («KS» = KeySwitch).
    /// </summary>
    public const ulong InjectionMarker = 0x4B53_0001;

    /// <summary>
    /// Флаг LLKHF_INJECTED системный: Windows выставляет его сама для любого SendInput.
    /// В dwFlags структуры KEYBDINPUT соответствующего флага нет, поэтому здесь он не задаётся.
    /// </summary>
    private static readonly int InputSize = Marshal.SizeOf<NativeMethods.INPUT>();

    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>Отправляет <paramref name="count"/> нажатий Backspace (по умолчанию — один пакет SendInput).</summary>
    public void SendBackspaces(int count)
    {
        if (count <= 0)
            return;

        var inputs = new NativeMethods.INPUT[count * 2];
        int index = 0;
        for (int i = 0; i < count; i++)
        {
            inputs[index++] = KeyInput((ushort)NativeMethods.VK_BACK, 0, 0);
            inputs[index++] = KeyInput((ushort)NativeMethods.VK_BACK, 0, NativeMethods.KEYEVENTF_KEYUP);
        }

        Send(inputs);
    }

    /// <summary>
    /// Вводит текст как Unicode-символы (KEYEVENTF_UNICODE). Символы BMP кодируются
    /// одним событием, суррогатные пары — двумя code unit'ами, как требует SendInput.
    /// </summary>
    public void SendText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var inputs = new NativeMethods.INPUT[text.Length * 2];
        int index = 0;
        foreach (char ch in text)
        {
            inputs[index++] = KeyInput(0, ch, NativeMethods.KEYEVENTF_UNICODE);
            inputs[index++] = KeyInput(0, ch, NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP);
        }

        Send(inputs);
    }

    /// <summary>
    /// Отправляет завершающий символ. Пробел, Enter и Tab идут через виртуальные клавиши
    /// (так их корректно видят приложения, ждущие WM_KEYDOWN), остальное — через Unicode.
    /// </summary>
    public void SendTerminator(char ch)
    {
        int virtualKey = ch switch
        {
            ' ' => NativeMethods.VK_SPACE,
            '\r' or '\n' => NativeMethods.VK_RETURN,
            '\t' => NativeMethods.VK_TAB,
            _ => 0
        };

        if (virtualKey == 0)
        {
            SendText(ch.ToString());
            return;
        }

        Send(new[]
        {
            KeyInput((ushort)virtualKey, 0, 0),
            KeyInput((ushort)virtualKey, 0, NativeMethods.KEYEVENTF_KEYUP)
        });
    }

    /// <summary>
    /// Отправляет одно событие клавиши: <paramref name="keyUp"/> = false — нажатие,
    /// true — отпускание. Если <paramref name="shift"/> задан, Shift удерживается
    /// вокруг события (down → событие → up), чтобы получить Shift+клавиша.
    /// </summary>
    public void SendKeyStroke(int virtualKey, bool shift, bool keyUp)
    {
        if (virtualKey <= 0)
            return;

        var events = new List<NativeMethods.INPUT>(3);
        if (shift)
            events.Add(KeyInput((ushort)NativeMethods.VK_SHIFT, 0, 0));

        events.Add(KeyInput(
            (ushort)virtualKey, 0, keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0u));

        if (shift)
            events.Add(KeyInput((ushort)NativeMethods.VK_SHIFT, 0, NativeMethods.KEYEVENTF_KEYUP));

        Send(events.ToArray());
    }

    /// <summary>
    /// Одиночный тап Shift — используется при переключении языка.
    /// </summary>
    public void SendShiftTap()
    {
        Send(new[]
        {
            KeyInput((ushort)NativeMethods.VK_SHIFT, 0, 0),
            KeyInput((ushort)NativeMethods.VK_SHIFT, 0, NativeMethods.KEYEVENTF_KEYUP)
        });
    }

    /// <summary>
    /// Пакет исправления: <paramref name="backspaces"/> удалений и текст одним
    /// вызовом SendInput. Контракт требует, чтобы между Backspace и текстом
    /// не было пользовательских событий; отдельные <see cref="SendBackspaces"/>
    /// и <see cref="SendText"/> такую гарантию не дают.
    /// </summary>
    public void SendCorrection(int backspaces, string text)
    {
        int deleteCount = Math.Max(0, backspaces);
        text ??= string.Empty;

        var inputs = new NativeMethods.INPUT[deleteCount * 2 + text.Length * 2];
        int index = 0;

        for (int i = 0; i < deleteCount; i++)
        {
            inputs[index++] = KeyInput((ushort)NativeMethods.VK_BACK, 0, 0);
            inputs[index++] = KeyInput((ushort)NativeMethods.VK_BACK, 0, NativeMethods.KEYEVENTF_KEYUP);
        }

        foreach (char ch in text)
        {
            inputs[index++] = KeyInput(0, ch, NativeMethods.KEYEVENTF_UNICODE);
            inputs[index++] = KeyInput(0, ch, NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP);
        }

        Send(inputs);
    }

    public void Dispose()
    {
        lock (_gate)
            _disposed = true;
    }

    // ------------------------------------------------------------ внутреннее

    private void Send(NativeMethods.INPUT[] inputs)
    {
        if (inputs.Length == 0)
            return;

        lock (_gate)
        {
            if (_disposed)
                return;

            // SendInput может вернуть меньше событий, если ввод заблокирован UIPI
            // или другим хуком. Ретраить вслепую опасно (можно задвоить текст),
            // поэтому просто возвращаем управление — вызывающий код не ждёт гарантий.
            NativeMethods.SendInput((uint)inputs.Length, inputs, InputSize);
        }
    }

    private static NativeMethods.INPUT KeyInput(ushort virtualKey, ushort scanCode, uint flags)
    {
        return new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            u = new NativeMethods.INPUTUNION
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = virtualKey,
                    wScan = scanCode,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = new UIntPtr(InjectionMarker)
                }
            }
        };
    }
}
