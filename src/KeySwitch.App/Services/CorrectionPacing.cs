namespace KeySwitch.App.Services;

/// <summary>
/// Браузеры поддерживают замену через выделение. Для остальных сценариев с
/// асинхронными редакторами сохраняем паузу между Backspace и вводом.
/// </summary>
public static class CorrectionPacing
{
    /// <summary>Пауза между удалением и вводом, мс (требование ТЗ: 10–15 мс).</summary>
    public const int SlowAppPauseMs = 12;

    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "yandex", "chromium"
    };

    private static readonly HashSet<string> SlowProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "slack", "discord", "telegram", "teams", "notion", "figma", "obsidian", "spotify",
        "whatsapp", "signal", "zoom", "element", "thunderbird"
    };

    public static bool IsBrowser(string? processName) => Browsers.Contains(WithoutExeExtension(processName));

    /// <summary>Нужна ли пауза для процесса (имя без .exe).</summary>
    public static int PauseFor(string? processName)
    {
        string name = WithoutExeExtension(processName);
        return Browsers.Contains(name) || SlowProcesses.Contains(name) ? SlowAppPauseMs : 0;
    }

    private static string WithoutExeExtension(string? name) =>
        name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true ? name[..^4] : name ?? string.Empty;
}
