namespace KeySwitch.Core.Correction;

/// <summary>Что именно исправляется.</summary>
public enum CorrectionKind
{
    /// <summary>Слово набрано не в той раскладке.</summary>
    LayoutSwitch,

    /// <summary>Опечатка по словарю замен.</summary>
    Typo,

    /// <summary>«ПРивет» → «Привет».</summary>
    CapsFix,

    /// <summary>Расстановка «ё» по однозначному словарю.</summary>
    YoFix,

    /// <summary>Ручная конвертация выделенного текста.</summary>
    Selection
}

/// <summary>
/// Готовый план исправления. Ядро не трогает ни клавиатуру, ни окна — оно только описывает,
/// что нужно сделать, а слой Hooks выполняет это через SendInput и смену раскладки.
/// </summary>
public sealed record CorrectionPlan(
    CorrectionKind Kind,
    string OriginalText,
    string ReplacementText,
    int DeleteCount,
    char? Terminator,
    string SourceLayoutId,
    string TargetLayoutId,
    double Confidence,
    string Reason)
{
    /// <summary>Строка, которая будет напечатана после удаления <see cref="DeleteCount"/> символов.</summary>
    public string TextToType => Terminator is { } t ? ReplacementText + t : ReplacementText;

    public override string ToString()
        => $"{Kind}: '{OriginalText}' → '{TextToType}' (del {DeleteCount}, {SourceLayoutId}→{TargetLayoutId}, {Confidence:P0})";
}

/// <summary>План отмены последнего исправления.</summary>
public sealed record UndoPlan(
    CorrectionKind Kind,
    string CurrentText,
    string RestoreText,
    int DeleteCount,
    string RestoreLayoutId,
    string CurrentLayoutId,
    char? Terminator)
{
    public string TextToType => Terminator is { } t ? RestoreText + t : RestoreText;
}
