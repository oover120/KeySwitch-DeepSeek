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
    /// <summary>Запускает проверку в фоне; <paramref name="log"/> получает строку для журнала.</summary>
    public static void CheckInBackground(Action<string>? log)
    {
        var thread = new Thread(() => Run(log))
        {
            IsBackground = true,
            Name = "KeySwitch.UpdateCheck",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static void Run(Action<string>? log)
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

        Show(info, current);
    }

    private static void Show(UpdateInfo info, Version current)
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

        MessageBoxResult answer = MessageBox.Show(
            Loc.Format("update.available", info.LatestVersion, current.ToString(3)),
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer == MessageBoxResult.Yes)
        {
            UpdateChecker.OpenDownloadPage(info.DownloadUrl ?? UpdateChecker.ReleasesPage);
        }
    }
}
