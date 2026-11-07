using KeySwitch.Core.Text;

namespace KeySwitch.Core.Input;

/// <summary>Роль нажатия с точки зрения буфера слова.</summary>
public enum KeyRole
{
    /// <summary>Символ может быть частью слова (буква, цифра, «спорный» знак препинания).</summary>
    WordChar,

    /// <summary>Клавиша завершает слово (пробел, Enter, Tab, однозначный знак препинания).</summary>
    Terminator,

    Backspace,
    Reset,
    Ignore
}

/// <summary>
/// Классификатор нажатий. Отдельно выделены «спорные» символы: в EN это знаки препинания,
/// а в RU на тех же физических клавишах — буквы (х ъ ж э б ю ё), поэтому они остаются в буфере
/// и разбираются детектором в конце слова.
/// </summary>
public static class KeyClassifier
{
    public const int VkBack = 0x08;
    public const int VkTab = 0x09;
    public const int VkReturn = 0x0D;
    public const int VkShift = 0x10;
    public const int VkControl = 0x11;
    public const int VkMenu = 0x12;
    public const int VkPause = 0x13;
    public const int VkCapital = 0x14;
    public const int VkEscape = 0x1B;
    public const int VkSpace = 0x20;
    public const int VkPrior = 0x21;
    public const int VkNext = 0x22;
    public const int VkEnd = 0x23;
    public const int VkHome = 0x24;
    public const int VkLeft = 0x25;
    public const int VkUp = 0x26;
    public const int VkRight = 0x27;
    public const int VkDown = 0x28;
    public const int VkSnapshot = 0x2C;
    public const int VkInsert = 0x2D;
    public const int VkDelete = 0x2E;
    public const int VkLWin = 0x5B;
    public const int VkRWin = 0x5C;

    public static KeyRole Classify(int virtualKey, bool ctrl, bool alt, bool win, char? character)
    {
        if (virtualKey is VkShift or VkControl or VkMenu or VkCapital or VkLWin or VkRWin)
            return KeyRole.Ignore;

        if (ctrl || alt || win)
            return KeyRole.Reset;

        switch (virtualKey)
        {
            case VkBack:
                return KeyRole.Backspace;
            case VkSpace:
            case VkReturn:
            case VkTab:
                return KeyRole.Terminator;
            case VkEscape:
            case VkHome:
            case VkEnd:
            case VkLeft:
            case VkRight:
            case VkUp:
            case VkDown:
            case VkDelete:
            case VkInsert:
            case VkPrior:
            case VkNext:
            case VkSnapshot:
                return KeyRole.Reset;
        }

        if (character is not { } ch)
            return KeyRole.Ignore;

        if (char.IsLetterOrDigit(ch))
            return KeyRole.WordChar;

        if (WordText.IsAmbiguousPunctuation(ch))
            return KeyRole.WordChar;

        return KeyRole.Terminator;
    }

    /// <summary>Может ли символ вообще быть началом слова (для отсева одиночных знаков).</summary>
    public static bool CanStartWord(char ch) => char.IsLetterOrDigit(ch) || WordText.IsAmbiguousPunctuation(ch);

    public static bool IsTerminatorChar(char ch) => ch is ' ' or '\r' or '\n' or '\t' || (!char.IsLetterOrDigit(ch) && !WordText.IsAmbiguousPunctuation(ch));
}
