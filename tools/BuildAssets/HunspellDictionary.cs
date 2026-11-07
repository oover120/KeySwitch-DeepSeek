using System.Text;

namespace KeySwitch.BuildAssets;

/// <summary>Строка Hunspell .dic: словоформа-лемма и её флаги.</summary>
internal sealed class DictionaryEntry
{
    public DictionaryEntry(string word, string[] flags)
    {
        Word = word;
        Flags = flags;
    }

    public string Word { get; }

    public string[] Flags { get; }

    public bool HasFlags => Flags.Length > 0;
}

/// <summary>
/// Разбор Hunspell .dic и минимальный экспандер аффиксов. Поддерживаются SFX/PFX с FLAG,
/// STRIP, ADD и CONDITION, режимы флагов short/long/num и кросс-произведение префикса
/// и суффикса (CROSS=Y). Продолжения флагов (continuation flags) в словарях en_US/ru_RU
/// не встречаются, поэтому двухуровневые аффиксы не поддерживаются — это осознанный компромисс
/// (см. docs/notes/data.md).
/// </summary>
internal static class HunspellDictionary
{
    public static List<DictionaryEntry> Load(string path, AffixFile affix)
    {
        var entries = new List<DictionaryEntry>(capacity: 200_000);

        foreach (var rawLine in File.ReadLines(path, Encoding.UTF8))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#')
                continue;

            // Строки с морфологической разметкой: "слово флаги po:...".
            var tab = line.IndexOf('\t');
            if (tab == 0)
                continue;
            if (tab > 0)
                line = line[..tab];

            line = line.Trim();
            if (line.Length == 0)
                continue;

            // Первая строка .dic — количество записей; нечисловые строки игнорируем.
            if (IsAllDigits(line))
                continue;

            var slash = line.IndexOf('/');
            var word = slash >= 0 ? line[..slash] : line;
            var flagsPart = slash >= 0 ? line[(slash + 1)..].Trim() : string.Empty;
            word = word.Trim();

            if (word.Length == 0)
                continue;

            entries.Add(new DictionaryEntry(word.ToLowerInvariant(), ParseFlags(flagsPart, affix.FlagMode)));
        }

        return entries;
    }

    /// <summary>Все словоформы леммы, которые даёт один уровень аффиксов (без самой леммы).</summary>
    public static List<string> Expand(string word, IReadOnlyList<string> flags, AffixFile affix, int maxForms)
    {
        var forms = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (flags.Count == 0 || maxForms <= 0)
            return forms;

        var suffixRules = new List<AffixRule>();
        var prefixRules = new List<AffixRule>();

        foreach (var flag in flags)
        {
            if (affix.Suffixes.TryGetValue(flag, out var suffixes))
                suffixRules.AddRange(suffixes);
            if (affix.Prefixes.TryGetValue(flag, out var prefixes))
                prefixRules.AddRange(prefixes);
        }

        foreach (var rule in suffixRules)
            Add(rule.TryApply(word));

        foreach (var rule in prefixRules)
            Add(rule.TryApply(word));

        // Кросс-произведение: Hunspell разрешает префикс+суффикс, если у обоих правил CROSS=Y.
        foreach (var prefix in prefixRules)
        {
            if (!prefix.Cross)
                continue;

            foreach (var suffix in suffixRules)
            {
                if (!suffix.Cross || forms.Count >= maxForms)
                    continue;

                var combined = TryApplyBoth(word, prefix, suffix);
                Add(combined);
            }
        }

        return forms;

        void Add(string? form)
        {
            if (form is null || form.Length == 0 || forms.Count >= maxForms)
                return;
            if (seen.Add(form))
                forms.Add(form);
        }
    }

    private static string? TryApplyBoth(string word, AffixRule prefix, AffixRule suffix)
    {
        if (!suffix.Condition.MatchesEnd(word) || !prefix.Condition.MatchesStart(word))
            return null;

        var middle = word;
        if (suffix.Strip.Length > 0)
        {
            if (!middle.EndsWith(suffix.Strip, StringComparison.Ordinal))
                return null;
            middle = middle[..^suffix.Strip.Length];
        }

        if (prefix.Strip.Length > 0)
        {
            if (!middle.StartsWith(prefix.Strip, StringComparison.Ordinal))
                return null;
            middle = middle[prefix.Strip.Length..];
        }

        return prefix.Add + middle + suffix.Add;
    }

    private static string[] ParseFlags(string flags, FlagMode mode)
    {
        if (flags.Length == 0)
            return Array.Empty<string>();

        switch (mode)
        {
            case FlagMode.Long:
            {
                var result = new List<string>(flags.Length / 2 + 1);
                for (var i = 0; i + 1 < flags.Length; i += 2)
                    result.Add(flags.Substring(i, 2));
                return result.ToArray();
            }

            case FlagMode.Num:
            {
                var parts = flags.Split(',', StringSplitOptions.RemoveEmptyEntries);
                var result = new List<string>(parts.Length);
                foreach (var part in parts)
                    result.Add(AffixFile.NormalizeFlag(part.Trim(), mode));
                return result.ToArray();
            }

            default:
            {
                var result = new string[flags.Length];
                for (var i = 0; i < flags.Length; i++)
                    result[i] = flags[i].ToString();
                return result;
            }
        }
    }

    private static bool IsAllDigits(string value)
    {
        foreach (var ch in value)
        {
            if (!char.IsDigit(ch))
                return false;
        }

        return value.Length > 0;
    }
}
