namespace KeySwitch.Core.Layout;

/// <summary>
/// Одно физическое нажатие клавиши: виртуальный код, состояние Shift и CapsLock,
/// аппаратный скан-код (нужен для повторной эмуляции через SendInput).
/// </summary>
public readonly record struct KeyStroke(int VirtualKey, bool Shift, bool CapsLock, int ScanCode = 0, bool Extended = false)
{
    public static KeyStroke None => new(0, false, false);

    public bool IsEmpty => VirtualKey == 0;

    public override string ToString() => $"VK=0x{VirtualKey:X2} shift={Shift} caps={CapsLock} sc=0x{ScanCode:X2}";
}
