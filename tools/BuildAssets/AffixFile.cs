using System.Text;

namespace KeySwitch.BuildAssets;

internal enum AffixKind
{
    Prefix,
    Suffix,
}

/// <summary>
/// Разобранное условие Hunspell (<c>CONDITION</c>): последовательность «любой символ», символьных
/// классов и литералов. Условие проверяется по началу (для префикса) или концу (для суффикса) основы.
/// Полноценные регулярные выражения здесь не нужны: в словарях LibreOffice встречаются только
/// <c>.</c>, литералы и классы вида <c>[^aeiou]</c>.
/// </summary>
internal sealed class ConditionPattern
{
    private readonly Token[] _tokens;

    private ConditionPattern(Token[] tokens) => _tokens = tokens;

    public static readonly ConditionPattern Any = new(Array.Empty<Token>());

    public bool IsEmpty => _tokens.Length == 0;

    public static ConditionPattern Parse(string? condition)
    {
        if (string.IsNullOrEmpty(condition) || condition == ".")
            return condition == "." ? new ConditionPattern(new[] { Token.AnyChar }) : Any;

        var tokens = new List<Token>(condition.Length);
        for (var i = 0; i < condition.Length; i++)
        {
            var ch = condition[i];
            if (ch == '[')
            {
                var close = condition.IndexOf(']', i + 1);
                if (close < 0)
                {
                    // Некорректная маска: считаем литералом, чтобы не терять правило.
                    tokens.Add(Token.Literal(ch));
                    continue;
                }

                tokens.Add(Token.ParseClass(condition[(i + 1)..close]));
                i = close;
            }
            else if (ch == '.')
            {
                tokens.Add(Token.AnyChar);
            }
            else
            {
                tokens.Add(Token.Literal(char.ToLowerInvariant(ch)));
            }
        }

        return new ConditionPattern(tokens.ToArray());
    }

    public bool MatchesStart(string word)
    {
        if (_tokens.Length == 0)
            return true;
        if (word.Length < _tokens.Length)
            return false;

        for (var i = 0; i < _tokens.Length; i++)
        {
            if (!_tokens[i].Matches(word[i]))
                return false;
        }

        return true;
    }

    public bool MatchesEnd(string word)
    {
        if (_tokens.Length == 0)
            return true;
        if (word.Length < _tokens.Length)
            return false;

        var offset = word.Length - _tokens.Length;
        for (var i = 0; i < _tokens.Length; i++)
        {
            if (!_tokens[i].Matches(word[offset + i]))
                return false;
        }

        return true;
    }

    private readonly struct Token
    {
        private readonly bool _any;
        private readonly bool _negated;
        private readonly char[]? _set;

        private Token(bool any, char[]? set, bool negated = false)
        {
            _any = any;
            _set = set;
            _negated = negated;
        }

        public static Token AnyChar => new(true, null);

        public static Token Literal(char ch) => new(false, new[] { ch });

        public static Token ParseClass(string content)
        {
            // Классы в наших словарях короткие; разворачиваем диапазоны в набор символов.
            // Отрицание ([^...]) встречается очень часто: без него теряется большинство
            // словоформ (например, «программист/K» → «программисты» задаётся классом [^егхйкожьшщч]).
            var negated = content.Length > 0 && content[0] == '^';
            if (negated)
                content = content[1..];

            var characters = new List<char>();
            for (var i = 0; i < content.Length; i++)
            {
                var ch = content[i];
                if (i + 2 < content.Length && content[i + 1] == '-')
                {
                    var from = char.ToLowerInvariant(ch);
                    var to = char.ToLowerInvariant(content[i + 2]);
                    for (var code = from; code <= to; code++)
                        characters.Add(code);
                    i += 2;
                }
                else if (ch != '^')
                {
                    characters.Add(char.ToLowerInvariant(ch));
                }
            }

            return new Token(false, characters.ToArray(), negated);
        }

        public bool Matches(char ch)
        {
            if (_any)
                return true;

            var match = Array.IndexOf(_set!, char.ToLowerInvariant(ch)) >= 0;
            return _negated ? !match : match;
        }
    }
}

/// <summary>Одно правило аффикса: FLAG / CROSS / STRIP / ADD / CONDITION.</summary>
internal sealed class AffixRule
{
    public AffixRule(string flag, bool cross, string strip, string add, ConditionPattern condition, AffixKind kind)
    {
        Flag = flag;
        Cross = cross;
        Strip = strip;
        Add = add;
        Condition = condition;
        Kind = kind;
    }

    public string Flag { get; }

    public bool Cross { get; }

    public string Strip { get; }

    public string Add { get; }

    public ConditionPattern Condition { get; }

    public AffixKind Kind { get; }

    /// <summary>Применяет правило к основе; null, если условие или STRIP не совпали.</summary>
    public string? TryApply(string stem)
    {
        if (Kind == AffixKind.Suffix)
        {
            if (!Condition.MatchesEnd(stem))
                return null;
            if (Strip.Length > 0)
            {
                if (!stem.EndsWith(Strip, StringComparison.Ordinal))
                    return null;
                stem = stem[..^Strip.Length];
            }

            return stem + Add;
        }

        if (!Condition.MatchesStart(stem))
            return null;
        if (Strip.Length > 0)
        {
            if (!stem.StartsWith(Strip, StringComparison.Ordinal))
                return null;
            stem = stem[Strip.Length..];
        }

        return Add + stem;
    }
}

/// <summary>
/// Минимальный разбор Hunspell .aff: секции SFX/PFX, режим FLAG (short/long/num).
/// Составные слова (COMPOUND*), ICONV и прочие директивы намеренно игнорируются —
/// для словарей-частот нам нужны только словоформы.
/// </summary>
internal sealed class AffixFile
{
    private readonly Dictionary<string, List<AffixRule>> _suffixes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<AffixRule>> _prefixes = new(StringComparer.Ordinal);

    private AffixFile()
    {
    }

    public FlagMode FlagMode { get; private set; } = FlagMode.Short;

    public IReadOnlyDictionary<string, List<AffixRule>> Suffixes => _suffixes;

    public IReadOnlyDictionary<string, List<AffixRule>> Prefixes => _prefixes;

    public static AffixFile Load(string path)
    {
        var affix = new AffixFile();
        AffixKind? currentKind = null;
        var currentFlag = string.Empty;
        var currentCross = false;

        foreach (var rawLine in File.ReadLines(path, Encoding.UTF8))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;

            switch (parts[0])
            {
                case "FLAG":
                    affix.FlagMode = parts[1].ToLowerInvariant() switch
                    {
                        "long" => FlagMode.Long,
                        "num" => FlagMode.Num,
                        _ => FlagMode.Short,
                    };
                    continue;

                case "SET":
                case "TRY":
                case "ICONV":
                case "WORDCHARS":
                case "COMPOUNDRULE":
                case "COMPOUNDMIN":
                case "ONLYINCOMPOUND":
                case "NOSUGGEST":
                case "FLAG:": // защита от экзотических записей
                    continue;

                case "SFX":
                case "PFX":
                    break;

                default:
                    continue;
            }

            var isSuffix = parts[0] == "SFX";
            var kind = isSuffix ? AffixKind.Suffix : AffixKind.Prefix;

            if (parts.Length >= 4 && int.TryParse(parts[3], out _))
            {
                // Заголовок группы: SFX FLAG CROSS COUNT.
                currentKind = kind;
                currentFlag = NormalizeFlag(parts[1], affix.FlagMode);
                currentCross = parts[2].Equals("Y", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (currentKind != kind || parts.Length < 4)
                continue;

            var flag = NormalizeFlag(parts[1], affix.FlagMode);
            if (!string.Equals(flag, currentFlag, StringComparison.Ordinal))
                continue;

            var strip = parts[2] == "0" ? string.Empty : parts[2];
            var add = parts[3] == "0" ? string.Empty : parts[3];
            var slash = add.IndexOf('/');
            if (slash >= 0)
                add = slash == 0 ? string.Empty : add[..slash]; // continuation-флаги не поддерживаем

            var condition = ConditionPattern.Parse(parts.Length >= 5 ? parts[4] : ".");

            var rule = new AffixRule(flag, currentCross, strip, add, condition, kind);
            var target = isSuffix ? affix._suffixes : affix._prefixes;
            if (!target.TryGetValue(flag, out var rules))
            {
                rules = new List<AffixRule>();
                target[flag] = rules;
            }

            rules.Add(rule);
        }

        return affix;
    }

    public static string NormalizeFlag(string flag, FlagMode mode) => mode switch
    {
        FlagMode.Long => flag.Length > 2 ? flag[..2] : flag,
        FlagMode.Num => flag.TrimStart('0') is { Length: > 0 } trimmed ? trimmed : "0",
        _ => flag.Length > 0 ? flag[..1] : flag,
    };
}

internal enum FlagMode
{
    Short,
    Long,
    Num,
}
