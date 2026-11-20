using KeySwitch.Core.Layout;
using KeySwitch.Core.Text;

namespace KeySwitch.Core.Detect;

/// <summary>
/// Детектор «не та раскладка». Сравнивает два прочтения одного и того же набора нажатий:
/// A — как набрано, B — после физической конвертации в соседнюю раскладку.
/// Переключение происходит только если B правдоподобно, а A — нет.
/// </summary>
public sealed class LayoutDetector
{
    private const int MinDictionaryFrequencyRatio = 100;
    private const int MinLengthForNgramCoverage = 5;
    private const double NgramCoverageThresholdFactor = 0.2;
    private const double DictionaryContextFactor = 0.8;
    private const double NgramContextFactor = 0.65;

    private readonly ILanguageResourcesProvider _resources;
    private readonly LayoutPairRegistry _pairs;
    private readonly DetectionSettings _settings;
    private readonly Dictionary<string, LanguageScorer> _scorers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Однобуквенные слова языка. Нужны для правила одиночных букв: «b» в русской раскладке —
    /// это «и», «c» — «с», но одиночная «a» (артикль) и «i» остаются как набраны.
    /// </summary>
    private static readonly Dictionary<string, HashSet<char>> OneLetterWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ru"] = new HashSet<char> { 'а', 'в', 'и', 'к', 'о', 'с', 'у', 'я' },
        ["en"] = new HashSet<char> { 'a', 'i' },
    };

    public LayoutDetector(ILanguageResourcesProvider resources, LayoutPairRegistry pairs, DetectionSettings settings)
    {
        _resources = resources;
        _pairs = pairs;
        _settings = settings;
    }

    public DetectionSettings Settings => _settings;

    public LayoutPairRegistry Pairs => _pairs;

    /// <summary>Проверяет пару «текущая раскладка → альтернативная».</summary>
    public DetectionResult Decide(string text, LayoutTable source)
    {
        var target = _pairs.GetAlternate(source);
        return target == null
            ? DetectionResult.Keep(text, source.Id, "нет пары раскладок")
            : Decide(text, source, target, early: false);
    }

    public DetectionResult Decide(string text, LayoutTable source, LayoutTable target, bool early,
        string? contextLanguage = null)
    {
        var letterCount = CountLetters(text);
        if (letterCount == 1)
            return DecideSingleLetter(text, source, target);
        if (letterCount < _settings.MinWordLength)
            return DetectionResult.Keep(text, source.Id, "слово слишком короткое");

        var sourceScorer = GetScorer(source.Language);
        var targetScorer = GetScorer(target.Language);
        if (sourceScorer == null || targetScorer == null)
            return DetectionResult.Keep(text, source.Id, "нет языковых моделей");

        var sourceScore = sourceScorer.Score(text);
        var (core, tail) = WordText.SplitTrailingPunctuation(text);
        if (sourceScore.InDictionary && (early || !_settings.UseFrequencyWeighting ||
            string.Equals(contextLanguage, source.Language, StringComparison.OrdinalIgnoreCase) ||
            source.Language != "en" || target.Language != "ru" || sourceScore.Frequency <= 0 ||
            !IsEnglishConsonantSequence(core)))
        {
            return KeepSourceDictionaryWord(text, source, sourceScore);
        }

        var fullText = LayoutConverter.Convert(text, source, target);
        if (string.Equals(fullText, text, StringComparison.Ordinal))
            return DetectionResult.Keep(text, source.Id, "символы не конвертируются", sourceScore.Total);

        var fullScore = targetScorer.Score(fullText);

        // Хвостовые знаки препинания: «ghbdtn,» — это «привет» + запятая,
        // а «[kt,» — это слово «хлеб», где запятая является буквой «б».
        var coreText = core;
        var coreScore = fullScore;
        if (tail.Length > 0 && core.Length > 0)
        {
            coreText = LayoutConverter.Convert(core, source, target) + tail;
            coreScore = targetScorer.Score(coreText);
        }

        var (candidateText, candidateScore, interpretation) = ChooseCandidate(fullText, fullScore, coreText, coreScore, tail);
        if (sourceScore.InDictionary)
        {
            // Английский словарь содержит и сокращения. Для них сравниваем оценки,
            // только если русское прочтение на два порядка чаще.
            if (!candidateScore.InDictionary || candidateScore.Frequency < (long)sourceScore.Frequency * MinDictionaryFrequencyRatio)
                return KeepSourceDictionaryWord(text, source, sourceScore);

            interpretation = "сравнение словарных вариантов";
        }

        var delta = candidateScore.Total - sourceScore.Total;

        var threshold = _settings.ConfidenceThreshold;
        if (!candidateScore.InDictionary)
            threshold *= _settings.NoDictionaryThresholdMultiplier;
        if (sourceScore.Penalty > 0)
            threshold *= 0.5;

        if (early)
        {
            threshold = Math.Max(threshold, _settings.EarlySwitchThreshold);
            if (!candidateScore.InDictionary)
                return DetectionResult.Keep(text, source.Id, "раннее переключение требует словарного слова", sourceScore.Total);
        }

        var contextSupportsTarget = !early && !sourceScore.InDictionary && candidateScore.Penalty == 0 &&
            string.Equals(contextLanguage, target.Language, StringComparison.OrdinalIgnoreCase);
        if (candidateScore.InDictionary && contextSupportsTarget && delta < threshold &&
            delta >= threshold * DictionaryContextFactor)
        {
            threshold *= DictionaryContextFactor;
            interpretation += ", с учётом контекста";
        }

        // Средние вероятности с общим floor слабо различают редкое слово и случайный
        // набор. Покрытие показывает, встречались ли сами сочетания букв в корпусе.
        // Если хвост допускает два прочтения, дополнительное правило не выбирает
        // между последней буквой и знаком препинания без словаря.
        var coverageThreshold = _settings.ConfidenceThreshold * _settings.NoDictionaryThresholdMultiplier *
                                NgramCoverageThresholdFactor;
        var contextualCoverageThreshold = contextSupportsTarget ? coverageThreshold * NgramContextFactor : coverageThreshold;
        var ngramAdvantage = candidateScore.NgramPerChar - sourceScore.NgramPerChar;
        if (!candidateScore.InDictionary && delta < threshold && delta >= contextualCoverageThreshold &&
            (tail.Length == 0 || string.Equals(fullText, coreText, StringComparison.Ordinal)) &&
            candidateScore.Penalty == 0 && ngramAdvantage >= contextualCoverageThreshold &&
            HasStrongNgramCoverage(core, candidateText, source, sourceScorer, targetScorer))
        {
            var needsContext = delta < coverageThreshold || ngramAdvantage < coverageThreshold;
            threshold = needsContext ? contextualCoverageThreshold : coverageThreshold;
            interpretation = needsContext ? "характерные сочетания букв и контекст" : "характерные сочетания букв";
        }

        var confidence = Sigmoid(delta - threshold);
        var reason = $"{interpretation}: Δ={delta:F2} (порог {threshold:F2}), " +
                     $"A={sourceScore.Total:F2}, B={candidateScore.Total:F2}";

        return new DetectionResult(delta >= threshold, confidence, source.Id, target.Id, text, candidateText, reason,
            sourceScore.Total, candidateScore.Total, sourceScore.InDictionary, candidateScore.InDictionary);
    }

    internal string? GetKnownLanguage(string text)
    {
        var (word, _) = WordText.SplitTrailingPunctuation(text);
        if (word.Length < 2 || word.Skip(1).Any(char.IsUpper))
            return null;

        var language = word.All(WordText.IsCyrillicLetter) ? "ru" : word.All(WordText.IsLatinLetter) ? "en" : null;
        return language != null && _resources.GetDictionary(language)?.Contains(WordText.Normalize(word)) == true
            ? language
            : null;
    }

    private static bool HasStrongNgramCoverage(string original, string candidateText,
        LayoutTable source, LanguageScorer sourceScorer, LanguageScorer targetScorer)
    {
        var (candidate, _) = WordText.SplitTrailingPunctuation(candidateText);
        if (candidate.Length < MinLengthForNgramCoverage || candidate.Length != original.Length ||
            !candidate.All(char.IsLetter))
        {
            return false;
        }

        // Сравниваем одни и те же клавиши. Смешанный регистр характерен для
        // идентификаторов и сокращений.
        if (original.Skip(1).Any(char.IsUpper) || !LayoutConverter.CanConvert(original, source))
            return false;

        var targetCoverage = targetScorer.GetNgramCoverage(candidate);
        if (targetCoverage.Trigrams < 0.85 || targetCoverage.Quadgrams < 0.60)
            return false;

        var sourceCoverage = sourceScorer.GetNgramCoverage(original);
        return targetCoverage.Trigrams - sourceCoverage.Trigrams >= 0.25 &&
               targetCoverage.Quadgrams - sourceCoverage.Quadgrams >= 0.50;
    }

    private static bool IsEnglishConsonantSequence(string core)
        => core.All(WordText.IsLatinLetter) && !core.Skip(1).Any(char.IsUpper)
            && core.AsSpan().IndexOfAny("aeiouyAEIOUY") < 0;

    private static DetectionResult KeepSourceDictionaryWord(string text, LayoutTable source, WordScore score)
        => DetectionResult.Keep(text, source.Id, "слово есть в словаре исходного языка", score.Total)
            with { SourceInDictionary = true };

    /// <summary>
    /// Принудительная конвертация по команде пользователя: тот же выбор прочтения, что и в
    /// <see cref="Decide(string, LayoutTable)"/>, но без порога уверенности. Нужна, когда детектор
    /// отказался переключать слово (незнакомое слово, код, аббревиатура), а человек уверен, что
    /// раскладка не та. Ограничения остаются только технические: минимальная длина и наличие пары.
    /// </summary>
    public DetectionResult ForceConvert(string text, LayoutTable source)
    {
        var target = _pairs.GetAlternate(source);
        if (target == null)
            return DetectionResult.Keep(text, source.Id, "нет пары раскладок");

        if (CountLetters(text) == 0)
            return DetectionResult.Keep(text, source.Id, "нет букв для конвертации");

        var fullText = LayoutConverter.Convert(text, source, target);
        if (string.Equals(fullText, text, StringComparison.Ordinal))
            return DetectionResult.Keep(text, source.Id, "символы не конвертируются");

        var targetScorer = GetScorer(target.Language);
        var (core, tail) = WordText.SplitTrailingPunctuation(text);
        var coreText = fullText;
        if (targetScorer == null)
            return new DetectionResult(true, 1.0, source.Id, target.Id, text, fullText,
                "принудительно (нет языковых моделей)", 0, 0, false, false);

        var fullScore = targetScorer.Score(fullText);
        var coreScore = fullScore;
        if (tail.Length > 0 && core.Length > 0)
        {
            coreText = LayoutConverter.Convert(core, source, target) + tail;
            coreScore = targetScorer.Score(coreText);
        }

        var (candidateText, candidateScore, interpretation) = ChooseCandidate(fullText, fullScore, coreText, coreScore, tail);
        return new DetectionResult(true, 1.0, source.Id, target.Id, text, candidateText,
            $"принудительно ({interpretation})", 0, candidateScore.Total, false, candidateScore.InDictionary);
    }

    /// <summary>
    /// Одиночная буква. Словарных оценок здесь нет — слово из одного символа слишком короткое
    /// для n-грамм, поэтому правило узкое и явное: конвертируем, только если исходная буква
    /// не является однобуквенным словом своего языка, а результат конвертации — однобуквенное
    /// слово целевого. Так «b» превращается в «и», а «a» и «i» остаются как набраны.
    /// </summary>
    private DetectionResult DecideSingleLetter(string text, LayoutTable source, LayoutTable target)
    {
        if (text.Length != 1 || !char.IsLetter(text[0]))
            return DetectionResult.Keep(text, source.Id, "слово слишком короткое");

        var letter = char.ToLowerInvariant(text[0]);
        if (IsOneLetterWord(source.Language, letter))
            return DetectionResult.Keep(text, source.Id, "однобуквенное слово исходного языка");

        var converted = LayoutConverter.Convert(text, source, target);
        if (converted.Length != 1 || !char.IsLetter(converted[0]))
            return DetectionResult.Keep(text, source.Id, "символ не конвертируется в букву");

        var convertedLetter = char.ToLowerInvariant(converted[0]);
        if (!IsOneLetterWord(target.Language, convertedLetter))
            return DetectionResult.Keep(text, source.Id, "нет однобуквенного слова в целевой раскладке");

        return new DetectionResult(true, 1.0, source.Id, target.Id, text, converted,
            $"однобуквенное слово: {letter} → {convertedLetter}", 0, 0, false, true);
    }

    private static bool IsOneLetterWord(string language, char letter)
    {
        var key = language.Length >= 2 ? language[..2] : language;
        return OneLetterWords.TryGetValue(key, out var set) && set.Contains(letter);
    }

    private static (string Text, WordScore Score, string Interpretation) ChooseCandidate(
        string fullText, WordScore fullScore, string coreText, WordScore coreScore, string tail)
    {
        if (fullScore.InDictionary)
            return (fullText, fullScore, "все символы — буквы");

        if (tail.Length > 0 && coreScore.InDictionary)
            return (coreText, coreScore, "хвост — знак препинания");

        return fullScore.Total >= coreScore.Total
            ? (fullText, fullScore, "по n-граммам (буквы)")
            : (coreText, coreScore, "по n-граммам (хвост — препинание)");
    }

    private LanguageScorer? GetScorer(string language)
    {
        if (_scorers.TryGetValue(language, out var scorer))
            return scorer;

        var dict = _resources.GetDictionary(language);
        var tri = _resources.GetTrigrams(language);
        var quad = _resources.GetQuadgrams(language);
        if (dict == null || tri == null || quad == null)
            return null;

        scorer = new LanguageScorer(dict, tri, quad, _settings);
        _scorers[language] = scorer;
        return scorer;
    }

    private static int CountLetters(string text)
    {
        var count = 0;
        foreach (var ch in text)
        {
            if (char.IsLetter(ch))
                count++;
        }
        return count;
    }

    private static double Sigmoid(double x) => 1.0 / (1.0 + Math.Exp(-x));
}
