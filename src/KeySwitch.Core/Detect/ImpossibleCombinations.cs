namespace KeySwitch.Core.Detect;

/// <summary>
/// Правила «невозможных» сочетаний символов. Это дешёвый и очень точный признак:
/// «ghbdtn» не может быть русским словом, а «ъь» — вообще ничем.
/// </summary>
public static class ImpossibleCombinations
{
    private const string RuVowels = "аеёиоуыэюя";
    private const string EnVowels = "aeiouy";

    public static (double Penalty, string? Rule) Check(string core, string language, ICharNgramModel? model)
    {
        if (core.Length == 0)
            return (0, null);

        return language switch
        {
            "ru" => CheckRussian(core),
            "en" => CheckEnglish(core),
            _ => (0, null)
        };
    }

    private static (double, string?) CheckRussian(string core)
    {
        if (core[0] is 'ь' or 'ъ')
            return (1.0, "слово не может начинаться с ь/ъ");

        if (core.Contains("ъь") || core.Contains("ьъ") || core.Contains("ъъ") || core.Contains("ьь"))
            return (1.0, "невозможное сочетание твёрдого и мягкого знаков");

        for (var i = 0; i < core.Length; i++)
        {
            if (core[i] != 'ъ')
                continue;
            var next = i + 1 < core.Length ? core[i + 1] : '\0';
            if (next is not ('е' or 'ё' or 'ю' or 'я' or 'и'))
                return (0.6, "твёрдый знак вне приставки");
        }

        var run = LongestConsonantRun(core, RuVowels);
        if (run >= 5)
            return (0.5, "слишком длинная цепочка согласных");

        return (0, null);
    }

    private static (double, string?) CheckEnglish(string core)
    {
        for (var i = 0; i < core.Length; i++)
        {
            if (core[i] != 'q')
                continue;
            var next = i + 1 < core.Length ? core[i + 1] : '\0';
            if (next != 'u' && next != '\0')
                return (0.8, "q без u");
        }

        if (core.Length >= 4 && !core.Any(c => EnVowels.Contains(c)))
            return (1.0, "нет ни одной гласной");

        var run = LongestConsonantRun(core, EnVowels);
        if (run >= 5)
            return (0.5, "слишком длинная цепочка согласных");

        if (core[0] is '\'' or '-')
            return (1.0, "слово не может начинаться со знака");

        return (0, null);
    }

    private static int LongestConsonantRun(string core, string vowels)
    {
        var best = 0;
        var current = 0;
        foreach (var ch in core)
        {
            if (!char.IsLetter(ch))
            {
                current = 0;
                continue;
            }

            if (vowels.IndexOf(ch) >= 0)
            {
                current = 0;
                continue;
            }

            current++;
            if (current > best)
                best = current;
        }

        return best;
    }
}
