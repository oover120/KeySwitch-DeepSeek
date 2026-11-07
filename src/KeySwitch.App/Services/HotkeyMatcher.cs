using KeySwitch.Core.Input;

namespace KeySwitch.App.Services;

/// <summary>
/// Горячая клавиша, разобранная из строки настроек, например "Ctrl+Shift+Space".
/// Сравнение строгое: все четыре модификатора должны совпасть, поэтому Ctrl+Shift+Space
/// не срабатывает на Ctrl+Alt+Shift+Space.
/// </summary>
public sealed class Hotkey
{
    internal Hotkey(int virtualKey, bool ctrl, bool alt, bool shift, bool win)
    {
        VirtualKey = virtualKey;
        Ctrl = ctrl;
        Alt = alt;
        Shift = shift;
        Win = win;
    }

    public int VirtualKey { get; }

    public bool Ctrl { get; }

    public bool Alt { get; }

    public bool Shift { get; }

    public bool Win { get; }

    public bool Matches(int virtualKey, bool ctrl, bool alt, bool shift, bool win)
        => virtualKey == VirtualKey && ctrl == Ctrl && alt == Alt && shift == Shift && win == Win;

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(HotkeyParser.DescribeKey(VirtualKey));
        return string.Join("+", parts);
    }

    /// <summary>Разбор строки. null — пустая или непонятная строка (горячая клавиша выключена).</summary>
    public static Hotkey? TryParse(string? spec) => HotkeyParser.Parse(spec);
}

public static class HotkeyParser
{
    private static readonly Dictionary<string, int> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["space"] = KeyClassifier.VkSpace,
        ["пробел"] = KeyClassifier.VkSpace,
        ["pause"] = KeyClassifier.VkPause,
        ["break"] = KeyClassifier.VkPause,
        ["esc"] = KeyClassifier.VkEscape,
        ["escape"] = KeyClassifier.VkEscape,
        ["tab"] = KeyClassifier.VkTab,
        ["enter"] = KeyClassifier.VkReturn,
        ["return"] = KeyClassifier.VkReturn,
        ["backspace"] = KeyClassifier.VkBack,
        ["bs"] = KeyClassifier.VkBack,
        ["insert"] = KeyClassifier.VkInsert,
        ["ins"] = KeyClassifier.VkInsert,
        ["delete"] = KeyClassifier.VkDelete,
        ["del"] = KeyClassifier.VkDelete,
        ["home"] = KeyClassifier.VkHome,
        ["end"] = KeyClassifier.VkEnd,
        ["pageup"] = KeyClassifier.VkPrior,
        ["pgup"] = KeyClassifier.VkPrior,
        ["pagedown"] = KeyClassifier.VkNext,
        ["pgdn"] = KeyClassifier.VkNext,
        ["up"] = KeyClassifier.VkUp,
        ["down"] = KeyClassifier.VkDown,
        ["left"] = KeyClassifier.VkLeft,
        ["right"] = KeyClassifier.VkRight
    };

    public static Hotkey? Parse(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return null;

        var ctrl = false;
        var alt = false;
        var shift = false;
        var win = false;
        var key = 0;

        foreach (var raw in spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                case "ctl":
                    ctrl = true;
                    continue;
                case "alt":
                    alt = true;
                    continue;
                case "shift":
                    shift = true;
                    continue;
                case "win":
                case "windows":
                case "meta":
                    win = true;
                    continue;
            }

            if (key != 0)
                return null; // две обычные клавиши в одной комбинации — ошибка

            key = ParseKey(raw);
        }

        return key == 0 ? null : new Hotkey(key, ctrl, alt, shift, win);
    }

    private static int ParseKey(string token)
    {
        if (NamedKeys.TryGetValue(token, out var named))
            return named;

        if (token.Length == 1)
        {
            var ch = char.ToUpperInvariant(token[0]);
            if (ch is >= 'A' and <= 'Z')
                return ch;
            if (ch is >= '0' and <= '9')
                return ch;
        }

        if (token.Length is 2 or 3 && (token[0] is 'f' or 'F') && int.TryParse(token[1..], out var fn) && fn is >= 1 and <= 24)
            return 0x70 + fn - 1;

        return 0;
    }

    public static string DescribeKey(int virtualKey)
    {
        if (virtualKey is >= 'A' and <= 'Z')
            return ((char)virtualKey).ToString();
        if (virtualKey is >= '0' and <= '9')
            return ((char)virtualKey).ToString();
        if (virtualKey is >= 0x70 and <= 0x87)
            return "F" + (virtualKey - 0x70 + 1);

        foreach (var pair in NamedKeys)
        {
            if (pair.Value == virtualKey && char.IsLetter(pair.Key[0]))
                return char.ToUpperInvariant(pair.Key[0]) + pair.Key[1..];
        }

        return "VK" + virtualKey;
    }
}
