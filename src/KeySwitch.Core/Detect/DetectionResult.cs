using KeySwitch.Core.Layout;

namespace KeySwitch.Core.Detect;

/// <summary>Результат работы детектора для одного слова.</summary>
public sealed record DetectionResult(
    bool Convert,
    double Confidence,
    string SourceLayoutId,
    string TargetLayoutId,
    string SourceText,
    string ConvertedText,
    string Reason,
    double SourceScore,
    double TargetScore,
    bool SourceInDictionary,
    bool TargetInDictionary)
{
    public static DetectionResult Keep(string text, string layoutId, string reason, double sourceScore = 0)
        => new(false, 0, layoutId, layoutId, text, text, reason, sourceScore, sourceScore, false, false);
}

/// <summary>Поставщик языковых ресурсов (реализуется слоем данных).</summary>
public interface ILanguageResourcesProvider
{
    IWordDictionary? GetDictionary(string language);

    ICharNgramModel? GetTrigrams(string language);

    ICharNgramModel? GetQuadgrams(string language);
}
