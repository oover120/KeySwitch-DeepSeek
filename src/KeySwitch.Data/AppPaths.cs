namespace KeySwitch.Data;

/// <summary>
/// Расположение пользовательских файлов KeySwitch. По умолчанию всё лежит в
/// <c>%APPDATA%\KeySwitch</c>; тесты и портативные сценарии создают объект с другим корнем.
/// Пути не создаются до вызова <see cref="EnsureCreated"/>, чтобы слой данных можно было
/// конструировать где угодно, не трогая диск.
/// </summary>
public sealed class AppPaths
{
    /// <summary>Корень по умолчанию: <c>%APPDATA%\KeySwitch</c>.</summary>
    public static AppPaths Default { get; } = new(DefaultRoot());

    public AppPaths(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Корневая папка не задана.", nameof(root));

        Root = Path.GetFullPath(root);
    }

    /// <summary>Корневая папка настроек и данных.</summary>
    public string Root { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string ExceptionsFile => Path.Combine(Root, "exceptions.json");

    public string LearningFile => Path.Combine(Root, "learning.json");

    public string TyposRuFile => Path.Combine(Root, "typos-ru.txt");

    public string TyposEnFile => Path.Combine(Root, "typos-en.txt");

    public string LogFile => Path.Combine(LogsDir, "keyswitch.log");

    /// <summary>Папка логов.</summary>
    public string LogsDir => Path.Combine(Root, "logs");

    /// <summary>Папка пользовательских звуков.</summary>
    public string SoundsDir => Path.Combine(Root, "sounds");

    /// <summary>Создаёт корневую папку, папку логов и папку звуков (идемпотентно).</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(SoundsDir);
    }

    private static string DefaultRoot()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            // Крайний случай (усечённый профиль): кладём рядом с профилем пользователя.
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            appData = string.IsNullOrWhiteSpace(userProfile)
                ? Path.GetTempPath()
                : Path.Combine(userProfile, ".config");
        }

        return Path.Combine(appData, "KeySwitch");
    }
}
