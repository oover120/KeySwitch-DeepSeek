namespace KeySwitch.Core.Text;

/// <summary>Небольшие текстовые помощники, общие для ядра и инструментов сборки данных.</summary>
public static class WordText
{
    /// <summary>Символы, которые в английской раскладке являются знаками препинания, а в русской — буквами.</summary>
    public const string AmbiguousPunctuation = "[];',.`";

    /// <summary>Все знаки препинания, которые могут стоять в конце набранного слова.</summary>
    public const string TrailingPunctuation = "[];',.`/\\!?\":(){}<>-—–_+*=№%&#@$^~|";

    public static string Normalize(string word) => word.ToLowerInvariant();

    public static bool IsLetterOrDigit(char ch) => char.IsLetterOrDigit(ch);

    public static bool IsCyrillicLetter(char ch) => ch is >= 'а' and <= 'я' or 'ё' or >= 'А' and <= 'Я' or 'Ё';

    public static bool IsLatinLetter(char ch) => ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    public static bool IsAmbiguousPunctuation(char ch) => AmbiguousPunctuation.IndexOf(ch) >= 0;

    /// <summary>
    /// Отделяет хвостовые знаки препинания от слова. Если весь текст состоит из знаков,
    /// возвращает пустое ядро (такие «слова» детектор игнорирует).
    /// </summary>
    public static (string Core, string Tail) SplitTrailingPunctuation(string text)
    {
        var end = text.Length;
        while (end > 0 && IsPunctuationLike(text[end - 1]))
            end--;

        // Не отрезаем знаки, если слева от них не осталось ни одной буквы или цифры.
        var hasCore = false;
        for (var i = 0; i < end; i++)
        {
            if (char.IsLetterOrDigit(text[i]))
            {
                hasCore = true;
                break;
            }
        }

        return hasCore ? (text[..end], text[end..]) : (text, string.Empty);
    }

    private static bool IsPunctuationLike(char ch)
    {
        if (char.IsLetterOrDigit(ch))
            return false;
        return ch is not ' ';
    }

    /// <summary>Меняет регистр строки, сохраняя позиции букв (используется при «ПРивет» → «Привет»).</summary>
    public static string CapitalizeFirst(string word)
    {
        if (word.Length == 0)
            return word;
        Span<char> buffer = stackalloc char[word.Length];
        word.AsSpan().CopyTo(buffer);
        buffer[0] = char.ToUpperInvariant(buffer[0]);
        for (var i = 1; i < buffer.Length; i++)
            buffer[i] = char.ToLowerInvariant(buffer[i]);
        return new string(buffer);
    }
}
