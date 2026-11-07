namespace KeySwitch.Core.Detect;

/// <summary>
/// Веса и пороги детектора. Значения подобраны на корпусах (см. tools/Evaluate и DEVLOG.md)
/// и могут быть переопределены из конфигурации пользователя.
/// </summary>
public sealed class DetectionSettings
{
    /// <summary>Минимальная длина слова, которое вообще рассматривается.</summary>
    public int MinWordLength { get; set; } = 2;

    /// <summary>Порог разницы оценок (в лог-единицах), после которого происходит переключение.</summary>
    public double ConfidenceThreshold { get; set; } = 2.0;

    /// <summary>Во сколько раз строже порог, если слово-кандидат отсутствует в словаре.</summary>
    public double NoDictionaryThresholdMultiplier { get; set; } = 2.5;

    /// <summary>Бонус за наличие слова в частотном словаре языка.</summary>
    public double DictionaryBonus { get; set; } = 3.0;

    /// <summary>Штраф за отсутствие слова в словаре языка.</summary>
    public double UnknownWordPenalty { get; set; } = 1.5;

    /// <summary>Вес символьной n-граммной модели (на символ).</summary>
    public double NgramWeight { get; set; } = 1.0;

    /// <summary>Штраф за нарушение правил невозможных сочетаний.</summary>
    public double ImpossiblePenalty { get; set; } = 8.0;

    /// <summary>Разрешать переключение до завершения слова (после N символов).</summary>
    public bool EarlySwitchEnabled { get; set; }

    /// <summary>Минимальная длина для раннего переключения.</summary>
    public int EarlySwitchMinLength { get; set; } = 4;

    /// <summary>Порог уверенности для раннего переключения — очень высокий.</summary>
    public double EarlySwitchThreshold { get; set; } = 6.0;

    /// <summary>Не переключать слова короче этого значения в режиме раннего срабатывания.</summary>
    public bool UseFrequencyWeighting { get; set; } = true;

    public DetectionSettings Clone() => (DetectionSettings)MemberwiseClone();
}
