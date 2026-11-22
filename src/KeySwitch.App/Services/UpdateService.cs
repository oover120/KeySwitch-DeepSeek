using System.Windows;
using KeySwitch.App.Localization;
using KeySwitch.Core.Updates;

namespace KeySwitch.App.Services;

/// <summary>
/// Пользовательский сценарий проверки обновлений: запрос к GitHub уходит только
/// по нажатию пункта меню, результат показывается в модальном окне на отдельном
/// STA-потоке — чтобы не блокировать ни колбэк хука, ни исполнитель плана.
/// </summary>
public static class UpdateService
{
    private static int _running;

    /// <summary>Запускает проверку в фоне; <paramref name="log"/> получает строку для журнала.</summary>
    public static void CheckInBackground(Action<string>? log, Action<string, string> notify)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0)
            return;

        var thread = new Thread(() =>
        {
            try { Run(log, notify); }
            finally { Interlocked.Exchange(ref _running, 0); }
        })
        {
            IsBackground = true,
            Name = "KeySwitch.UpdateCheck",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static void Run(Action<string>? log, Action<string, string> notify)
    {
        Version current = UpdateChecker.CurrentVersion;
        UpdateInfo info;

        try
        {
            info = UpdateChecker.CheckAsync(current).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            info = UpdateInfo.Failure(ex.Message);
        }

        if (log is not null)
        {
            if (info.Error is not null)
            {
                log($"Проверка обновлений не удалась: {info.Error}");
            }
            else if (info.HasUpdate)
            {
                log($"Доступно обновление {info.LatestVersion} (установлена {current.ToString(3)}).");
            }
            else
            {
                log($"Обновлений нет: установлена последняя версия {current.ToString(3)}.");
            }
        }

        Show(info, current, log, notify);
    }

    private static void Show(UpdateInfo info, Version current, Action<string>? log, Action<string, string> notify)
    {
        string title = Loc.T("update.title");

        if (info.Error is not null)
        {
            MessageBox.Show(
                Loc.Format("update.failed", info.Error),
                title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!info.HasUpdate)
        {
            MessageBox.Show(
                Loc.Format("update.upToDate", current.ToString(3)),
                title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            UpdatePackage? package = info.Installer;
            InstalledUpdate? installed = package is null ? null : InstalledUpdate.Find();
            string prompt = installed is null ? "update.available" : "update.install";
            MessageBoxResult answer = MessageBox.Show(
                Loc.Format(prompt, info.LatestVersion, current.ToString(3)),
                title, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
                return;

            if (installed is null || package is null)
            {
                UpdateChecker.OpenDownloadPage();
                return;
            }

            notify(title, Loc.Format("update.downloading", info.LatestVersion));
            installed.DownloadAndStartAsync(package).GetAwaiter().GetResult();
            log?.Invoke($"Запущено обновление до {info.LatestVersion}.");
            var application = System.Windows.Application.Current;
            application.Dispatcher.Invoke(() => application.Shutdown());
        }
        catch (Exception ex)
        {
            log?.Invoke($"Не удалось запустить обновление: {ex.Message}");
            string reason = ex is OperationCanceledException ? Loc.T("update.timeout") : ex.Message;
            MessageBoxResult answer = MessageBox.Show(Loc.Format("update.installFailed", reason),
                title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes)
                UpdateChecker.OpenDownloadPage();
        }
    }
}
