namespace KeySwitch.App.Services;

/// <summary>
/// Тема оформления. Объявлена в App.Services (а не в Data), потому что это понятие
/// представления: слой данных хранит только строку настройки Theme.
/// </summary>
public enum AppTheme
{
    /// <summary>Как в системе (светлая/тёмная по параметрам Windows).</summary>
    System = 0,

    Light = 1,
    Dark = 2
}

public static class AppThemeExtensions
{
    /// <summary>Разбирает значение из настроек: "system"/"light"/"dark" или число.</summary>
    public static AppTheme Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return AppTheme.System;

        if (int.TryParse(value, out var number) && Enum.IsDefined(typeof(AppTheme), number))
            return (AppTheme)number;

        return value.Trim().ToLowerInvariant() switch
        {
            "light" or "светлая" => AppTheme.Light,
            "dark" or "тёмная" or "темная" => AppTheme.Dark,
            _ => AppTheme.System
        };
    }

    /// <summary>Строка для сохранения в настройках.</summary>
    public static string ToSettingValue(this AppTheme theme) => theme switch
    {
        AppTheme.Light => "light",
        AppTheme.Dark => "dark",
        _ => "system"
    };
}
