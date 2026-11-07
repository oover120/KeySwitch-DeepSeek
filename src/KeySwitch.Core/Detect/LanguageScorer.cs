using KeySwitch.Core.Text;

namespace KeySwitch.Core.Detect;

/// <summary>Оценка слова для одного языка.</summary>
public readonly record struct WordScore(
    double Total,
    double NgramPerChar,
    bool InDictionary,
    int Frequency,
    double Penalty,
    string? RuleViolation)
{
    public static WordScore Neutral => new(0, 0, false, 0, 0, null);
}

/// <summary>
/// Скоринг слова: словарь + символьная n-граммная модель + правила невозможных сочетаний.
/// Все три признака независимы, поэтому модель легко расширяется третьим языком.
/// </summary>
public sealed class LanguageScorer
{
    private readonly IWordDictionary _dictionary;
    private readonly ICharNgramModel _trigrams;
    private readonly ICharNgramModel _quadgrams;
    private readonly DetectionSettings _settings;

    public LanguageScorer(IWordDictionary dictionary, ICharNgramModel trigrams, ICharNgramModel quadgrams, DetectionSettings settings)
    {
        _dictionary = dictionary;
        _trigrams = trigrams;
        _quadgrams = quadgrams;
        _settings = settings;
    }

    public string Language => _dictionary.Language;

    public WordScore Score(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return WordScore.Neutral;

        var normalized = WordText.Normalize(text);
        var (core, _) = WordText.SplitTrailingPunctuation(normalized);
        if (core.Length == 0)
            return WordScore.Neutral;

        // Дефисные и апострофные слова оцениваем по частям: цельная форма в словаре
        // обычно отсутствует, а каждая часть — обычное слово.
        if (core.Contains('-') || core.Contains('\'') || core.Contains('’'))
            return ScoreCompound(core);

        var (ngram, inDict, frequency) = ScoreCore(core);
        var (penalty, rule) = ImpossibleCombinations.Check(core, Language, _trigrams);
        var total = ngram * _settings.NgramWeight
                    + (inDict ? _settings.DictionaryBonus : -_settings.UnknownWordPenalty)
                    - penalty;

        return new WordScore(total, ngram, inDict, frequency, penalty, rule);
    }

    private WordScore ScoreCompound(string core)
    {
        var parts = core.Split(new[] { '-', '\'', '’' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1)
            return BuildSimple(core);

        double total = 0;
        double ngramSum = 0;
        var allInDict = true;
        var frequency = 0;
        var penalty = 0.0;
        string? rule = null;

        foreach (var part in parts)
        {
            var (ngram, inDict, freq) = ScoreCore(part);
            var (p, r) = ImpossibleCombinations.Check(part, Language, _trigrams);
            ngramSum += ngram;
            allInDict &= inDict;
            frequency = Math.Max(frequency, freq);
            penalty += p;
            rule ??= r;
        }

        var average = ngramSum / parts.Length;
        total = average * _settings.NgramWeight
                + (allInDict ? _settings.DictionaryBonus : -_settings.UnknownWordPenalty)
                - penalty;
        return new WordScore(total, average, allInDict, frequency, penalty, rule);
    }

    private WordScore BuildSimple(string core)
    {
        var (ngram, inDict, frequency) = ScoreCore(core);
        var (penalty, rule) = ImpossibleCombinations.Check(core, Language, _trigrams);
        var total = ngram * _settings.NgramWeight
                    + (inDict ? _settings.DictionaryBonus : -_settings.UnknownWordPenalty)
                    - penalty;
        return new WordScore(total, ngram, inDict, frequency, penalty, rule);
    }

    private (double NgramPerChar, bool InDictionary, int Frequency) ScoreCore(string core)
    {
        var inDict = _dictionary.Contains(core);
        var frequency = inDict ? _dictionary.GetFrequency(core) : -1;

        double sum = 0;
        var count = 0;

        if (_trigrams.Order > 0 && core.Length + 2 >= 3)
        {
            NgramHasher.ForEachGram(core, 3, hash =>
            {
                sum += _trigrams.GetLogProbHash(hash);
                count++;
            });
        }

        if (_quadgrams.Order > 0 && core.Length + 2 >= 4)
        {
            NgramHasher.ForEachGram(core, 4, hash =>
            {
                sum += _quadgrams.GetLogProbHash(hash);
                count++;
            });
        }

        var perChar = count > 0 ? sum / count : -4.0;
        return (perChar, inDict, frequency);
    }
}
