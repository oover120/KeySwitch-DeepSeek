namespace KeySwitch.Hooks;

/// <summary>
/// Правила, по которым окно считается «игрой» и автопереключение в нём отключается.
/// Вынесено из <c>AutoSwitchService</c> отдельным классом без Win32-зависимостей,
/// чтобы правило можно было покрыть юнит-тестами.
/// </summary>
public static class WindowClassification
{
    /// <summary>
    /// Процессы, у которых полноэкранный режим — штатный способ просмотра, а не игра:
    /// F11, полноэкранное видео, PWA. Имена без расширения — так их возвращает
    /// <see cref="System.Diagnostics.Process.ProcessName"/>.
    /// </summary>
    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "yandex",
        "browser", "thorium", "librewolf", "waterfox",
    };

    /// <summary>Известный браузер (Chrome, Edge, Firefox и родственные).</summary>
    public static bool IsBrowserProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return false;

        var name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        return BrowserProcesses.Contains(name);
    }

    /// <summary>
    /// Полноэкранное окно, в котором автопереключение надо выключить. Браузер в полноэкранном
    /// режиме игрой не считается: окно Chrome в F11 занимает ровно весь монитор и не имеет
    /// заголовка, из-за чего проверка полноэкранного окна принимала его за игру.
    /// </summary>
    public static bool IsFullscreenGame(ForegroundWindowInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return info.IsFullscreen && !IsBrowserProcess(info.ProcessName);
    }
}
