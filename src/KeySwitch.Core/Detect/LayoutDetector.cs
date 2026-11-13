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

    public DetectionResult Decide(string text, LayoutTable source, LayoutTable target, bool early)
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
        if (sourceScore.InDictionary)
            return DetectionResult.Keep(text, source.Id, "слово есть в словаре исходного языка", sourceScore.Total);

        var fullText = LayoutConverter.Convert(text, source, target);
        if (string.Equals(fullText, text, StringComparison.Ordinal))
            return DetectionResult.Keep(text, source.Id, "символы не конвертируются", sourceScore.Total);

        var fullScore = targetScorer.Score(fullText);

        // Хвостовые знаки препинания: «ghbdtn,» — это «привет» + запятая,
        // а «[kt,» — это слово «хлеб», где запятая является буквой «б».
        var (core, tail) = WordText.SplitTrailingPunctuation(text);
        var coreText = core;
        var coreScore = fullScore;
        if (tail.Length > 0 && core.Length > 0)
        {
            coreText = LayoutConverter.Convert(core, source, target) + tail;
            coreScore = targetScorer.Score(coreText);
        }

        var (candidateText, candidateScore, interpretation) = ChooseCandidate(fullText, fullScore, coreText, coreScore, tail);
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

        var confidence = Sigmoid(delta - threshold);
        var reason = $"{interpretation}: Δ={delta:F2} (порог {threshold:F2}), " +
                     $"A={sourceScore.Total:F2}, B={candidateScore.Total:F2}";

        if (delta < threshold)
            return new DetectionResult(false, confidence, source.Id, target.Id, text, candidateText, reason,
                sourceScore.Total, candidateScore.Total, false, candidateScore.InDictionary);

        return new DetectionResult(true, confidence, source.Id, target.Id, text, candidateText, reason,
            sourceScore.Total, candidateScore.Total, false, candidateScore.InDictionary);
    }

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
