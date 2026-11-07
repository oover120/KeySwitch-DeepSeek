using System.Runtime.InteropServices;

namespace KeySwitch.Hooks;

/// <summary>
/// Результат одного пакета <c>SendInput</c>.
/// </summary>
/// <param name="Sent">Сколько событий просили вставить.</param>
/// <param name="Inserted">Сколько событий система приняла (возврат SendInput).</param>
/// <param name="LastError">GetLastError после вызова (0 — ошибки нет).</param>
public readonly record struct SendResult(int Sent, int Inserted, int LastError)
{
    /// <summary>Все события приняты системой.</summary>
    public bool Complete => Inserted >= Sent;

    /// <summary>Сколько событий не принято.</summary>
    public int Lost => Math.Max(0, Sent - Inserted);
}

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

    /// <summary>Отправляет <paramref name="count"/> нажатий Backspace (один пакет SendInput).</summary>
    public SendResult SendBackspaces(int count)
    {
        if (count <= 0)
            return new SendResult(0, 0, 0);

        return Send(BuildBackspaces(count));
    }

    /// <summary>
    /// Вводит текст как Unicode-символы (KEYEVENTF_UNICODE). Символы BMP кодируются
    /// одним событием, суррогатные пары — двумя code unit'ами, как требует SendInput.
    /// </summary>
    public SendResult SendText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return new SendResult(0, 0, 0);

        return Send(BuildText(text));
    }

    /// <summary>
    /// Отправляет завершающий символ. Пробел, Enter и Tab идут через виртуальные клавиши
    /// (так их корректно видят приложения, ждущие WM_KEYDOWN), остальное — через Unicode.
    /// </summary>
    public SendResult SendTerminator(char ch)
    {
        if (ch == default)
            return new SendResult(0, 0, 0);

        return Send(BuildTerminator(ch));
    }

    /// <summary>
    /// Отправляет одно событие клавиши: <paramref name="keyUp"/> = false — нажатие,
    /// true — отпускание. Если <paramref name="shift"/> задан, Shift удерживается
    /// вокруг события (down → событие → up), чтобы получить Shift+клавиша.
    /// </summary>
    public SendResult SendKeyStroke(int virtualKey, bool shift, bool keyUp)
    {
        if (virtualKey <= 0)
            return new SendResult(0, 0, 0);

        var events = new List<NativeMethods.INPUT>(3);
        if (shift)
            events.Add(KeyInput((ushort)NativeMethods.VK_SHIFT, 0, 0));

        events.Add(KeyInput(
            (ushort)virtualKey, 0, keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0u));

        if (shift)
            events.Add(KeyInput((ushort)NativeMethods.VK_SHIFT, 0, NativeMethods.KEYEVENTF_KEYUP));

        return Send(events.ToArray());
    }

    /// <summary>
    /// Одиночный тап Shift — используется при переключении языка.
    /// </summary>
    public SendResult SendShiftTap()
    {
        return Send(new[]
        {
            KeyInput((ushort)NativeMethods.VK_SHIFT, 0, 0),
            KeyInput((ushort)NativeMethods.VK_SHIFT, 0, NativeMethods.KEYEVENTF_KEYUP)
        });
    }

    /// <summary>
    /// Пакет исправления: <paramref name="backspaces"/> удалений и текст с терминатором
    /// одним вызовом SendInput (при <paramref name="pauseMs"/> = 0).
    ///
    /// Почему одним вызовом: между Backspace и текстом не должно вклиниться ни одно
    /// пользовательское событие, иначе удалится или вставится лишний символ.
    /// Для браузеров и Electron-приложений пауза задаётся явно — там между удалением
    /// и вводом нужно дать странице обработать изменения (иначе часть Backspace теряется).
    /// </summary>
    public SendResult SendCorrection(int backspaces, string text, char? terminator, int pauseMs = 0)
    {
        int deleteCount = Math.Max(0, backspaces);
        text ??= string.Empty;

        if (pauseMs <= 0)
        {
            var inputs = new List<NativeMethods.INPUT>(deleteCount * 2 + text.Length * 2 + 2);
            AddBackspaces(inputs, deleteCount);
            AddText(inputs, text);
            AddTerminator(inputs, terminator);
            return Send(inputs.ToArray());
        }

        // Двухфазный режим: сначала стираем, затем (после паузы) печатаем.
        var erase = Send(BuildBackspaces(deleteCount));
        if (erase.Lost > 0)
            return erase;

        Thread.Sleep(pauseMs);

        var typing = new List<NativeMethods.INPUT>(text.Length * 2 + 2);
        AddText(typing, text);
        AddTerminator(typing, terminator);
        return Send(typing.ToArray());
    }

    /// <summary>
    /// Текст и завершающий символ одним пакетом SendInput (для двухфазного режима,
    /// когда удаление уже отправлено отдельно).
    /// </summary>
    public SendResult SendTextAndTerminator(string text, char? terminator)
    {
        text ??= string.Empty;
        var inputs = new List<NativeMethods.INPUT>(text.Length * 2 + 2);
        AddText(inputs, text);
        AddTerminator(inputs, terminator);
        return Send(inputs.ToArray());
    }

    public void Dispose()
    {
        lock (_gate)
            _disposed = true;
    }

    // ------------------------------------------------------------ внутреннее

    /// <summary>
    /// Один вызов SendInput с однократным повтором неотправленного хвоста.
    /// Повторяем именно хвост: SendInput возвращает число принятых событий,
    /// и повтор всего пакета задвоил бы уже вставленный текст.
    /// </summary>
    private SendResult Send(NativeMethods.INPUT[] inputs)
    {
        if (inputs.Length == 0)
            return new SendResult(0, 0, 0);

        lock (_gate)
        {
            if (_disposed)
                return new SendResult(inputs.Length, 0, 0);

            uint inserted = NativeMethods.SendInput((uint)inputs.Length, inputs, InputSize);
            int error = inserted == inputs.Length ? 0 : Marshal.GetLastWin32Error();
            var result = new SendResult(inputs.Length, (int)inserted, error);

            if (result.Complete || inserted == 0 || inserted > inputs.Length)
                return result;

            // Повтор только для непринятого хвоста.
            var rest = new NativeMethods.INPUT[inputs.Length - (int)inserted];
            Array.Copy(inputs, (int)inserted, rest, 0, rest.Length);

            uint retried = NativeMethods.SendInput((uint)rest.Length, rest, InputSize);
            int retryError = retried == rest.Length ? 0 : Marshal.GetLastWin32Error();
            return new SendResult(
                inputs.Length,
                (int)(inserted + retried),
                retryError != 0 ? retryError : error);
        }
    }

    private static NativeMethods.INPUT[] BuildBackspaces(int count)
    {
        var inputs = new NativeMethods.INPUT[count * 2];
        int index = 0;
        for (int i = 0; i < count; i++)
        {
            inputs[index++] = KeyInput((ushort)NativeMethods.VK_BACK, 0, 0);
            inputs[index++] = KeyInput((ushort)NativeMethods.VK_BACK, 0, NativeMethods.KEYEVENTF_KEYUP);
        }

        return inputs;
    }

    private static NativeMethods.INPUT[] BuildText(string text)
    {
        var inputs = new NativeMethods.INPUT[text.Length * 2];
        int index = 0;
        foreach (char ch in text)
        {
            inputs[index++] = KeyInput(0, ch, NativeMethods.KEYEVENTF_UNICODE);
            inputs[index++] = KeyInput(0, ch, NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP);
        }

        return inputs;
    }

    private static NativeMethods.INPUT[] BuildTerminator(char ch)
    {
        int virtualKey = VirtualKeyOf(ch);
        if (virtualKey == 0)
            return BuildText(ch.ToString());

        return new[]
        {
            KeyInput((ushort)virtualKey, 0, 0),
            KeyInput((ushort)virtualKey, 0, NativeMethods.KEYEVENTF_KEYUP)
        };
    }

    private static void AddBackspaces(List<NativeMethods.INPUT> inputs, int count)
    {
        for (int i = 0; i < count; i++)
        {
            inputs.Add(KeyInput((ushort)NativeMethods.VK_BACK, 0, 0));
            inputs.Add(KeyInput((ushort)NativeMethods.VK_BACK, 0, NativeMethods.KEYEVENTF_KEYUP));
        }
    }

    private static void AddText(List<NativeMethods.INPUT> inputs, string text)
    {
        foreach (char ch in text)
        {
            inputs.Add(KeyInput(0, ch, NativeMethods.KEYEVENTF_UNICODE));
            inputs.Add(KeyInput(0, ch, NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP));
        }
    }

    private static void AddTerminator(List<NativeMethods.INPUT> inputs, char? terminator)
    {
        if (terminator is not { } ch || ch == default)
            return;

        int virtualKey = VirtualKeyOf(ch);
        if (virtualKey == 0)
        {
            AddText(inputs, ch.ToString());
            return;
        }

        inputs.Add(KeyInput((ushort)virtualKey, 0, 0));
        inputs.Add(KeyInput((ushort)virtualKey, 0, NativeMethods.KEYEVENTF_KEYUP));
    }

    private static int VirtualKeyOf(char ch) => ch switch
    {
        ' ' => NativeMethods.VK_SPACE,
        '\r' or '\n' => NativeMethods.VK_RETURN,
        '\t' => NativeMethods.VK_TAB,
        _ => 0
    };

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
