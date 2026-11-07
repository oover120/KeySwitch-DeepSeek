namespace KeySwitch.Data;

/// <summary>
/// Пользовательские настройки. Все поля имеют осмысленный дефолт (программа работает
/// сразу после первого запуска), сериализуются в camelCase и переживают добавление
/// новых полей — отсутствующие в файле свойства остаются дефолтными.
/// </summary>
public sealed class AppSettings
{
    // ── Основное ────────────────────────────────────────────────────────────
    /// <summary>Общий выключатель автопереключения.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Автоматически исправлять раскладку (иначе только вручную).</summary>
    public bool AutoSwitch { get; set; } = true;

    // ── Раннее переключение ─────────────────────────────────────────────────
    public bool EarlySwitchEnabled { get; set; } = false;

    public int EarlySwitchMinLength { get; set; } = 4;

    public double EarlySwitchThreshold { get; set; } = 6.0;

    // ── Горячие клавиши и отмена ────────────────────────────────────────────
    public bool SingleShiftSwitch { get; set; } = false;

    public bool DoubleShiftUndo { get; set; } = true;

    /// <summary>Строка вида "Ctrl+Shift+Space"; пустая строка — горячая клавиша выключена.</summary>
    public string ManualConvertHotkey { get; set; } = "Ctrl+Shift+Space";

    /// <summary>Обработка Pause/Break: отмена последнего исправления или ручная конвертация.</summary>
    public bool PauseHotkeyEnabled { get; set; } = true;

    // ── Автозапуск ──────────────────────────────────────────────────────────
    public bool StartWithWindows { get; set; } = false;

    public bool RunAsAdmin { get; set; } = false;

    // ── Пороги детектора ────────────────────────────────────────────────────
    public double ConfidenceThreshold { get; set; } = 2.0;

    public double NoDictionaryThresholdMultiplier { get; set; } = 2.5;

    // ── Постобработка ───────────────────────────────────────────────────────
    public bool TyposEnabled { get; set; } = true;

    public bool CapsFixEnabled { get; set; } = true;

    public bool YoFixEnabled { get; set; } = true;

    // ── Звуки ───────────────────────────────────────────────────────────────
    public bool SoundsEnabled { get; set; } = false;

    /// <summary>Путь к wav-файлу звука переключения; пустая строка — звука нет.</summary>
    public string SwitchSoundPath { get; set; } = string.Empty;

    /// <summary>Путь к wav-файлу звука исправления; пустая строка — звука нет.</summary>
    public string FixSoundPath { get; set; } = string.Empty;

    /// <summary>Громкость 0..1.</summary>
    public double SoundVolume { get; set; } = 0.7;

    // ── Защита и режимы ─────────────────────────────────────────────────────
    public bool GameDetectionEnabled { get; set; } = true;

    public bool PasswordProtectionEnabled { get; set; } = true;

    public bool SafeMode { get; set; } = false;

    // ── Интерфейс ───────────────────────────────────────────────────────────
    /// <summary>"system" | "light" | "dark".</summary>
    public string Theme { get; set; } = "system";

    /// <summary>"ru" | "en" | "system" — язык интерфейса.</summary>
    public string Language { get; set; } = "system";

    // ── Обучение ────────────────────────────────────────────────────────────
    public bool LearningEnabled { get; set; } = true;

    /// <summary>Сколько отмен подряд нужно, чтобы слово перестало исправляться.</summary>
    public int UndoThresholdToSuppress { get; set; } = 3;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>Приводит значения к допустимым диапазонам (вызывается после чтения настроек).</summary>
    internal void Normalize()
    {
        if (EarlySwitchMinLength < 2)
            EarlySwitchMinLength = 2;

        if (ConfidenceThreshold <= 0)
            ConfidenceThreshold = 2.0;

        if (NoDictionaryThresholdMultiplier < 1)
            NoDictionaryThresholdMultiplier = 1;

        if (EarlySwitchThreshold <= ConfidenceThreshold)
            EarlySwitchThreshold = ConfidenceThreshold * 2;

        if (UndoThresholdToSuppress < 1)
            UndoThresholdToSuppress = 3;

        SoundVolume = Math.Clamp(SoundVolume, 0, 1);

        if (string.IsNullOrWhiteSpace(ManualConvertHotkey))
            ManualConvertHotkey = string.Empty;

        Theme = string.IsNullOrWhiteSpace(Theme) ? "system" : Theme.Trim().ToLowerInvariant();
        Language = string.IsNullOrWhiteSpace(Language) ? "system" : Language.Trim().ToLowerInvariant();
    }
}
