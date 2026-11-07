using System.Windows;
using System.Windows.Threading;
using KeySwitch.App.Services;
using KeySwitch.App.Localization;
using KeySwitch.App.Themes;
using KeySwitch.Data;

// Псевдонимы: Application и MessageBox встречаются в нескольких пространствах имён .NET,
// а приложению нужны именно WPF-варианты.
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace KeySwitch.App;

/// <summary>
/// Точка входа. Главного окна нет: приложение стартует в трее, единственное окно —
/// настройки, которое создаётся по требованию.
/// </summary>
public partial class App : Application
{
    private AppLogger? _logger;
    private AppHost? _host;
    private SingleInstance? _singleInstance;
    private bool _startupFailed;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Один экземпляр на сессию/машину. Второй запуск просто активирует первый.
        _singleInstance = new SingleInstance();
        if (!_singleInstance.TryAcquire())
        {
            SingleInstance.SignalExistingInstance();
            Shutdown();
            return;
        }

        _singleInstance.ActivationRequested += OnActivationRequested;
        _singleInstance.StartListening();

        AppPaths paths;
        try
        {
            paths = AppPaths.Default;
            paths.EnsureCreated();
            _logger = new AppLogger(paths);
        }
        catch (Exception ex)
        {
            // Язык из настроек ещё не применён (настройки не загрузились) — Loc отдаст
            // русский текст по умолчанию, это и есть безопасный fallback.
            MessageBox.Show(
                Loc.Format("startup.data_folder.failed", ex.Message),
                Loc.T("app.name"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
            return;
        }

        RegisterGlobalExceptionHandlers();

        try
        {
            _host = new AppHost(paths, _logger);

            // Язык и тему применяем до первых окон и до значка в трее.
            Loc.Instance.SetLanguage(_host.Settings.Language);

            // Тему применяем до первых окон. App.xaml намеренно пустой — словари кладёт ThemeManager.
            ThemeManager.EnsureInitialized(_host.Settings.Theme);

            _logger.Info($"KeySwitch DeepSeek запущен (v{typeof(App).Assembly.GetName().Version}).");
            // Путь к исполняемому файлу — единственный надёжный способ понять, какая именно
            // сборка сейчас работает: на машине могут одновременно лежать несколько копий,
            // и все они пишут в один и тот же журнал.
            _logger.Info($"Файл приложения: {Environment.ProcessPath}");
            if (_host.Settings.SafeMode)
                _logger.Warn("Безопасный режим: перехват клавиатуры отключён.");

            _host.Start();
        }
        catch (Exception ex)
        {
            _startupFailed = true;
            _logger.Error("Критическая ошибка при запуске", ex);
            MessageBox.Show(
                Loc.Format("startup.failed", @"%APPDATA%\KeySwitch\logs\keyswitch.log"),
                Loc.T("app.name"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                _logger?.Error("Необработанное исключение", ex);
            else
                _logger?.Error($"Необработанное исключение: {args.ExceptionObject}");
        };

        DispatcherUnhandledException += (_, args) =>
        {
            // Не роняем приложение из-за ошибки в обработчике UI: пишем в журнал и живём дальше.
            _logger?.Error("Исключение в UI-потоке", args.Exception);
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger?.Error("Исключение в фоновой задаче", args.Exception);
            args.SetObserved();
        };
    }

    private void OnActivationRequested(object? sender, EventArgs e)
    {
        // Событие приходит из пула потоков — окно можно открывать только из UI-потока.
        Dispatcher.BeginInvoke(
            new Action(() => _host?.OpenSettings()),
            DispatcherPriority.Normal);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _host?.Dispose();
        }
        catch (Exception ex)
        {
            _logger?.Error("Ошибка при завершении приложения", ex);
        }

        try
        {
            _singleInstance?.Dispose();
        }
        catch (Exception)
        {
            // ignore
        }

        if (!_startupFailed)
            _logger?.Info("Приложение остановлено.");

        _logger?.Flush();

        base.OnExit(e);
    }
}
