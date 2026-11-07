using System.Text;

namespace KeySwitch.Core.Layout;

/// <summary>
/// Конвертация текста между раскладками по физическим клавишам.
/// Символ, которого нет в исходной таблице (цифры, эмодзи, знаки из других языков),
/// остаётся без изменений.
/// </summary>
public static class LayoutConverter
{
    public static string Convert(string text, LayoutTable from, LayoutTable to)
    {
        if (string.IsNullOrEmpty(text) || ReferenceEquals(from, to))
            return text;

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
            sb.Append(ConvertChar(ch, from, to));
        return sb.ToString();
    }

    public static char ConvertChar(char ch, LayoutTable from, LayoutTable to)
    {
        if (!from.TryGetKey(ch, out var vk, out var shift))
            return ch;
        return to.TryMap(vk, shift, out var mapped) ? mapped : ch;
    }

    /// <summary>Все ли символы строки имеют физическую клавишу в исходной раскладке.</summary>
    public static bool CanConvert(string text, LayoutTable from)
    {
        foreach (var ch in text)
        {
            if (!from.TryGetKey(ch, out _, out _))
                return false;
        }
        return true;
    }

    /// <summary>Количество символов, которые не удастся перевести.</summary>
    public static int CountUnmapped(string text, LayoutTable from)
    {
        var count = 0;
        foreach (var ch in text)
        {
            if (!from.TryGetKey(ch, out _, out _))
                count++;
        }
        return count;
    }
}
