namespace KeySwitch.Hooks;

/// <summary>
/// Правила, по которым окно считается «игрой» и автопереключение в нём отключается.
/// Вынесено из <c>AutoSwitchService</c> отдельным классом без Win32-зависимостей,
/// чтобы правило можно было покрыть юнит-тестами.
/// </summary>
/// <remarks>
/// Игру нельзя определить по одному признаку: у игр нет общего API, которое сказало бы
/// «я игра». Поэтому складываются три независимых признака, и любого достаточно:
/// <list type="number">
/// <item>полноэкранное окно (эксклюзивный полный экран и borderless, см. <see cref="WindowInspector"/>);</item>
/// <item>путь к программе внутри игровой библиотеки (Steam, Epic, GOG, Battle.net и т.п.);</item>
/// <item>класс окна игрового движка (Unity, Unreal, SDL, GLFW, CryEngine).</item>
/// </list>
/// Игра в окне с обычной рамкой, лежащая вне игровых папок, остаётся неотличимой от
/// приложения — это осознанное ограничение, а не забытый случай.
/// </remarks>
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

    /// <summary>
    /// Каталоги игровых библиотек. Проверяется подстрока пути, поэтому «\Games\» ловит и
    /// «C:\Games\…», и «D:\My Games\…», а «\steamapps\common\» — любую библиотеку Steam,
    /// включая вторую на другом диске.
    /// </summary>
    private static readonly string[] GameDirectoryMarkers =
    [
        @"\Games\",
        @"\steamapps\common\",
        @"\Epic Games\",
        @"\GOG Games\",
        @"\GOG Galaxy\Games\",
        @"\Battle.net\",
        @"\Riot Games\",
        @"\Ubisoft\",
        @"\Ubisoft Game Launcher\games\",
        @"\EA Games\",
        @"\Origin Games\",
        @"\XboxGames\",
        @"\Minecraft\",
    ];

    /// <summary>
    /// Классы окон игровых движков: у Unity это UnityWndClass, у Unreal — UnrealWindow,
    /// у SDL/GLFW — свои короткие имена. Класс окна стабильнее имени процесса: он не меняется
    /// от переименования exe и одинаков у сотен игр.
    /// </summary>
    private static readonly HashSet<string> GameWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "UnityWndClass",
        "UnrealWindow",
        "SDL_app",
        "GLFW30",
        "GLFW31",
        "CryENGINE",
        "CrySystem",
        "RiotWindowClass",
        "R6Game",
        "Valve001",
        "DishonoredWindowClass",
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

    /// <summary>Программа лежит в игровой библиотеке (Steam, Epic, GOG, Battle.net, Games).</summary>
    public static bool IsGameDirectory(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
            return false;

        var path = processPath.Replace('/', '\\');
        foreach (var marker in GameDirectoryMarkers)
        {
            if (path.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Окно принадлежит известному игровому движку.</summary>
    public static bool IsGameWindowClass(string? className)
    {
        if (string.IsNullOrWhiteSpace(className))
            return false;

        return GameWindowClasses.Contains(className.Trim());
    }

    /// <summary>
    /// Итоговое правило: игра — если полноэкранное окно (кроме браузеров), либо программа
    /// из игровой папки, либо окно игрового движка.
    /// </summary>
    public static bool IsGame(ForegroundWindowInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        if (IsFullscreenGame(info))
            return true;

        if (IsGameDirectory(info.ProcessPath))
            return true;

        // Класс окна проверяем последним: он бесплатный, но решает случаи, которые не ловят
        // первые два правила, — например, Unity-игра в окне, установленная вне игровых папок.
        return IsGameWindowClass(info.ClassName);
    }
}
