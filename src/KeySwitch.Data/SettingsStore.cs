namespace KeySwitch.Data;

/// <summary>
/// Хранилище настроек: читает/пишет <see cref="AppPaths.SettingsFile"/>, чинит битый файл
/// и оповещает подписчиков об успешном сохранении. <see cref="Current"/> — живой изменяемый
/// экземпляр: UI правит его поля, затем вызывает <see cref="Save"/>.
/// </summary>
public sealed class SettingsStore
{
    private readonly AppPaths _paths;
    private readonly AppLogger _logger;

    public SettingsStore(AppPaths paths, AppLogger logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Current = new AppSettings();
    }

    /// <summary>Текущие настройки (изменяемый экземпляр).</summary>
    public AppSettings Current { get; private set; }

    /// <summary>Вызывается после успешной записи файла настроек.</summary>
    public event EventHandler? Saved;

    /// <summary>
    /// Загружает настройки. Файла нет — создаётся дефолтный; файл повреждён — он
    /// переименовывается в <c>settings.json.bad</c>, а настройки сбрасываются в дефолт.
    /// </summary>
    public void Load()
    {
        _paths.EnsureCreated();

        if (!File.Exists(_paths.SettingsFile))
        {
            _logger.Info("Настройки не найдены, создаю файл по умолчанию.");
            Current = new AppSettings();
            TrySave();
            return;
        }

        var loaded = JsonFile.TryRead<AppSettings>(_paths.SettingsFile, out var error);
        if (loaded is null)
        {
            var bad = JsonFile.Quarantine(_paths.SettingsFile);
            _logger.Warn($"Файл настроек повреждён ({error}), переименован в {Path.GetFileName(bad)}, беру значения по умолчанию.");
            Current = new AppSettings();
            TrySave();
            return;
        }

        loaded.Normalize();
        Current = loaded;
        _logger.Info("Настройки загружены.");
    }

    /// <summary>Записывает <see cref="Current"/> на диск атомарно и поднимает <see cref="Saved"/>.</summary>
    public void Save()
    {
        Current.Normalize();
        JsonFile.Write(_paths.SettingsFile, Current);
        _logger.Debug("Настройки сохранены.");
        Saved?.Invoke(this, EventArgs.Empty);
    }

    private void TrySave()
    {
        try
        {
            Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error("Не удалось сохранить настройки по умолчанию.", ex);
        }
    }
}
