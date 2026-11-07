namespace KeySwitch.App.Services;

/// <summary>
/// Пауза между стиранием и вводом для приложений, которые не успевают обработать
/// пачку Backspace за один кадр: браузеры и Electron-приложения с асинхронной
/// отрисовкой. Без паузы часть удалений теряется, и исправление портит текст.
/// </summary>
public static class CorrectionPacing
{
    /// <summary>Пауза между удалением и вводом, мс (требование ТЗ: 10–15 мс).</summary>
    public const int SlowAppPauseMs = 12;

    private static readonly HashSet<string> SlowProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        // Браузеры
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "yandex", "chromium",
        // Electron и веб-обёртки
        "slack", "discord", "telegram", "teams", "notion", "figma", "obsidian", "spotify",
        "whatsapp", "signal", "zoom", "element", "thunderbird"
    };

    /// <summary>Нужна ли пауза для процесса (имя без .exe).</summary>
    public static int PauseFor(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return 0;

        string name = processName;
        int dot = name.LastIndexOf('.');
        if (dot > 0 && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..dot];

        return SlowProcesses.Contains(name) ? SlowAppPauseMs : 0;
    }
}
