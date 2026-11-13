using KeySwitch.App.Localization;
using KeySwitch.App.Themes;
using KeySwitch.App.Tray;
using KeySwitch.App.ViewModels;
using KeySwitch.App.Views;
using KeySwitch.Core.Detect;
using KeySwitch.Core.Engine;
using KeySwitch.Core.Layout;
using KeySwitch.Core.PostProcess;
using KeySwitch.Data;
using KeySwitch.Hooks;

namespace KeySwitch.App.Services;

/// <summary>
/// Собирает приложение из сервисов и владеет их временем жизни.
///
/// Порядок запуска: трей появляется сразу (чтобы приложение было видно и управляемо),
/// настройки автозапуска синхронизируются с ОС, словари грузятся в фоне, и только после
/// этого запускается хук клавиатуры — детектору нужны словари, а старт не должен ждать диск.
/// </summary>
public sealed class AppHost : IDisposable
{
    private readonly AppPaths _paths;
    private readonly AppLogger _logger;
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;

    private readonly ExceptionStore _exceptions;
    private readonly LearningStore _learning;
    private readonly TypoDictionary _typos;
    private readonly YoDictionary _yo;
    private readonly DeferredLanguageProvider _languageProvider;
    private readonly LayoutDetector _detector;
    private readonly CorrectionEngine _engine;
    private readonly KeyboardHook _hook;
    private readonly InputSender _sender;
    private readonly LayoutSwitcher _layouts;
    private readonly ClipboardHelper _clipboard;
    private readonly ForegroundTracker _tracker;
    private readonly ForegroundWatcher _foreground;
    private readonly PlanExecutor _executor;
    private readonly SoundService _sound;
    private readonly AutoSwitchService _autoSwitch;
    private readonly AutostartService _autostart;
    private readonly TrayService _tray;

    private SettingsViewModel? _settingsViewModel;
    private SettingsWindow? _settingsWindow;
    private bool _started;
    private bool _disposed;

    public AppHost(AppPaths paths, AppLogger logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _store = new SettingsStore(_paths, _logger);
        _store.Load();
        _settings = _store.Current;

        _exceptions = new ExceptionStore(_paths, _logger, resources: null);
        _exceptions.Load();
        _learning = new LearningStore(_paths, _logger, _settings, _exceptions);
        _typos = new TypoDictionary(_paths, _logger);
        _yo = new YoDictionary(_logger);

        _languageProvider = new DeferredLanguageProvider();
        var pairs = LayoutPairRegistry.CreateDefault();
        var detectionSettings = new DetectionSettings
        {
            EarlySwitchEnabled = _settings.EarlySwitchEnabled,
            EarlySwitchMinLength = _settings.EarlySwitchMinLength,
            EarlySwitchThreshold = _settings.EarlySwitchThreshold,
            ConfidenceThreshold = _settings.ConfidenceThreshold,
            NoDictionaryThresholdMultiplier = _settings.NoDictionaryThresholdMultiplier,
        };

        _detector = new LayoutDetector(_languageProvider, pairs, detectionSettings);

        var processors = new List<IPostProcessor>
        {
            new ToggleablePostProcessor(new CapsFixer(), () => _settings.CapsFixEnabled),
            new ToggleablePostProcessor(new TypoCorrector(_typos), () => _settings.TyposEnabled),
            new ToggleablePostProcessor(new YoFixer(_yo), () => _settings.YoFixEnabled),
        };

        _engine = new CorrectionEngine(
            _detector,
            detectionSettings,
            new SettingsAwareLearningStore(_learning, _settings),
            _exceptions,
            new PostProcessorPipeline(processors));

        _hook = new KeyboardHook();
        _sender = new InputSender();
        _layouts = new LayoutSwitcher();
        _clipboard = new ClipboardHelper();
        _tracker = new ForegroundTracker(new WindowInspector(), new UiAutomationProbe(), _logger);
        _executor = new PlanExecutor(_sender, _layouts, _logger);
        _sound = new SoundService(_logger);
        _sound.SetVolume(_settings.SoundVolume);

        // Хук сообщает о собственных задержках: медленный колбэк Windows может снять.
        _hook.Diagnostics = _logger.Warn;

        // Смена активного окна прогревает кэш (процесс, раскладка, проверка пароля)
        // заранее — к первому нажатию всё уже готово, а хук ничего не вычисляет.
        _foreground = new ForegroundWatcher();
        _foreground.ForegroundChanged += OnForegroundChanged;

        _autoSwitch = new AutoSwitchService(
            _settings,
            _store,
            _engine,
            _executor,
            _hook,
            _sender,
            _layouts,
            _clipboard,
            _tracker,
            _exceptions,
            _sound,
            _logger);

        _autostart = new AutostartService(_settings, _store, _logger);

        _tray = new TrayService(_settings, _autoSwitch, _tracker, _exceptions, _autostart, _logger);
        _tray.SettingsRequested += (_, _) => OpenSettings();
        _tray.ExceptionsRequested += (_, _) => OpenSettings();
        _tray.ExitRequested += (_, _) => System.Windows.Application.Current?.Shutdown();
        _tray.UpdateRequested += (_, _) => UpdateService.CheckInBackground(_logger.Info);
    }

    public AppSettings Settings => _settings;

    /// <summary>Трей для уведомлений (например, если хук не удалось запустить).</summary>
    public TrayService Tray => _tray;

    /// <summary>Запускает фоновые части: автозапуск, словари, хук.</summary>
    public void Start()
    {
        if (_started || _disposed)
            return;

        _started = true;

        try
        {
            _autostart.Synchronize();
        }
        catch (Exception ex)
        {
            _logger.Error("Синхронизация автозапуска не удалась", ex);
        }

        if (_settings.SafeMode)
        {
            _logger.Warn("Включён безопасный режим: хук клавиатуры не запускается.");
            return;
        }

        // Словари грузятся в фоне: старт приложения не должен ждать чтения мегабайтов с диска.
        Task.Run(LoadLanguageResources);
    }

    private void LoadLanguageResources()
    {
        try
        {
            LanguageResources resources = LanguageResources.Load(_logger);
            _languageProvider.Inner = resources;
            _exceptions.AttachResources(resources);
            _logger.Info("Языковые ресурсы загружены.");

            try
            {
                _exceptions.WarmFrequentWords();
            }
            catch (Exception ex)
            {
                _logger.Debug($"WarmFrequentWords: {ex.Message}");
            }

            // Загрузка словарей создаёт крупные временные буферы (распаковка gzip, склейка
            // больших строк). Они попадают в LOH, который обычный GC не уплотняет, и висят
            // в рабочем наборе до конца жизни процесса. После загрузки они не нужны: одна
            // полная компактирующая сборка возвращает ~20 МБ и держит простое в бюджете.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

            Post(StartHook);
        }
        catch (Exception ex)
        {
            _logger.Error("Не удалось загрузить языковые ресурсы", ex);
            // Без словарей детектор работает только на эвристиках — это лучше, чем не работать вовсе.
            Post(StartHook);
        }
    }

    private void Post(Action action)
    {
        try
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                action();
                return;
            }

            dispatcher.BeginInvoke(action);
        }
        catch (Exception ex)
        {
            _logger.Debug($"AppHost.Post: {ex.Message}");
        }
    }

    private void StartHook()
    {
        if (_disposed || _settings.SafeMode)
            return;

        try
        {
            _foreground.Start();
            _autoSwitch.Start();
        }
        catch (Exception ex)
        {
            _logger.Error("Не удалось запустить перехват клавиатуры", ex);
            _tray.ShowBalloon(
                Loc.T("app.name"),
                Loc.T("balloon.hook.failed"),
                TrayBalloonIcon.Error);
        }
    }

    /// <summary>
    /// Сменилось активное окно: кэш процесса, раскладки и проверки пароля прогревается
    /// заранее, чтобы первое нажатие не тратило на это время.
    /// </summary>
    private void OnForegroundChanged(IntPtr handle)
    {
        try
        {
            _tracker.OnForegroundChanged(handle);
        }
        catch (Exception ex)
        {
            _logger.Debug($"OnForegroundChanged: {ex.Message}");
        }
    }

    /// <summary>Открывает окно настроек (единственный экземпляр).</summary>
    public void OpenSettings()
    {
        if (_disposed)
            return;

        if (_settingsWindow is { IsLoaded: true })
        {
            try
            {
                _settingsWindow.Activate();
            }
            catch (Exception)
            {
                // ignore
            }

            return;
        }

        try
        {
            if (_settingsViewModel is null)
            {
                _settingsViewModel = new SettingsViewModel(_settings, _exceptions, _learning, _paths, _logger);
                _settingsViewModel.Saved += OnSettingsSaved;
            }
            else
            {
                _settingsViewModel.Reload();
            }

            var window = new SettingsWindow(_settingsViewModel);
            _settingsWindow = window;

            try
            {
                window.ShowDialog();
            }
            finally
            {
                _settingsWindow = null;
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Не удалось открыть окно настроек", ex);
            _settingsWindow = null;
        }
    }

    /// <summary>
    /// Окно настроек записало значения в <see cref="AppSettings"/>/исключения и подняло Saved.
    /// Здесь сохраняем settings.json и применяем изменения к работающим сервисам.
    /// </summary>
    private void OnSettingsSaved(object? sender, EventArgs e)
    {
        try
        {
            _store.Save();
        }
        catch (Exception ex)
        {
            _logger.Error("Не удалось сохранить настройки", ex);
        }

        try
        {
            _sound.SetVolume(_settings.SoundVolume);
            _engine.ClearUndo();
            _autoSwitch.Reconfigure();
            ThemeManager.Apply(_settings.Theme);
            _autostart.Apply();
            _tray.RefreshNow();
            _logger.Info("Настройки применены.");
        }
        catch (Exception ex)
        {
            _logger.Error("Не удалось применить настройки", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            if (_settingsViewModel is not null)
                _settingsViewModel.Saved -= OnSettingsSaved;
        }
        catch (Exception)
        {
            // ignore
        }

        try
        {
            _tray.Dispose();
        }
        catch (Exception)
        {
            // ignore
        }

        try
        {
            _foreground.Dispose();
        }
        catch (Exception)
        {
            // ignore
        }

        try
        {
            _autoSwitch.Dispose();
        }
        catch (Exception)
        {
            // ignore
        }

        try
        {
            _hook.Dispose();
        }
        catch (Exception)
        {
            // ignore
        }

        try
        {
            _executor.Dispose();
        }
        catch (Exception)
        {
            // ignore
        }

        try
        {
            _sender.Dispose();
        }
        catch (Exception)
        {
            // ignore
        }

        // ClipboardHelper сам владеет STA-потоком и завершается вместе с процессом,
        // отдельного Dispose у него нет.

        try
        {
            _sound.Dispose();
        }
        catch (Exception)
        {
            // ignore
        }

        try
        {
            _store.Save();
        }
        catch (Exception ex)
        {
            _logger.Error("Не удалось сохранить настройки при выходе", ex);
        }

        _logger.Info("KeySwitch DeepSeek завершил работу.");
        _logger.Flush();
    }
}
