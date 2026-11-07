namespace KeySwitch.Hooks;

/// <summary>
/// Одно событие низкоуровневого хука клавиатуры. Создаётся на потоке хука,
/// обработчик вызывается синхронно и обязан быть быстрым (единицы миллисекунд).
/// </summary>
public sealed class KeyboardHookEventArgs : EventArgs
{
    /// <summary>
    /// Создаёт событие. Публичный конструктор нужен не только хуку, но и тестам/эмуляции:
    /// порядок параметров совпадает с порядком свойств в контракте.
    /// </summary>
    public KeyboardHookEventArgs(
        int virtualKey,
        int scanCode,
        bool isKeyDown,
        bool isInjected = false,
        bool isOurEvent = false,
        bool shift = false,
        bool ctrl = false,
        bool alt = false,
        bool win = false,
        bool capsLock = false,
        char? character = null)
    {
        VirtualKey = virtualKey;
        ScanCode = scanCode;
        IsKeyDown = isKeyDown;
        IsInjected = isInjected;
        IsOurEvent = isOurEvent;
        Shift = shift;
        Ctrl = ctrl;
        Alt = alt;
        Win = win;
        CapsLock = capsLock;
        Character = character;
    }

    /// <summary>Виртуальный код клавиши (VK_*).</summary>
    public int VirtualKey { get; }

    /// <summary>Аппаратный скан-код клавиши.</summary>
    public int ScanCode { get; }

    /// <summary>true — нажатие, false — отпускание.</summary>
    public bool IsKeyDown { get; }

    /// <summary>Событие сгенерировано программно (LLKHF_INJECTED), но не нами.</summary>
    public bool IsInjected { get; }

    /// <summary>
    /// Событие отправлено самим KeySwitch (<see cref="InputSender.InjectionMarker"/>).
    /// Такие события в <see cref="KeyboardHook.KeyEvent"/> не попадают, флаг нужен для диагностики.
    /// </summary>
    public bool IsOurEvent { get; }

    public bool Shift { get; }

    public bool Ctrl { get; }

    public bool Alt { get; }

    public bool Win { get; }

    public bool CapsLock { get; }

    /// <summary>Символ, который ввёл бы это нажатие в активной раскладке; null, если символа нет.</summary>
    public char? Character { get; }

    /// <summary>true — событие не доходит до приложения (хук возвращает 1).</summary>
    public bool Handled { get; set; }

    /// <summary>
    /// Диагностическое представление. Символ намеренно не выводится: содержимое ввода
    /// не должно попадать в логи.
    /// </summary>
    public override string ToString()
    {
        return $"VK=0x{VirtualKey:X2} scan=0x{ScanCode:X2} {(IsKeyDown ? "down" : "up")} " +
               $"shift={Shift} ctrl={Ctrl} alt={Alt} win={Win} caps={CapsLock} " +
               $"injected={IsInjected} handled={Handled}";
    }
}
