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
