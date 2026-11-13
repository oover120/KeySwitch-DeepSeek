using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using KeySwitch.Core.Updates;

namespace KeySwitch.App.Services;

/// <summary>
/// Ручная проверка обновлений через публичный API GitHub.
/// Сеть используется только по явному нажатию пользователя: ни фоновых опросов,
/// ни телеметрии. Ничего о пользователе не отправляется — только GET релиза.
/// Разбор ответа живёт в ядре (<see cref="ReleaseParser"/>) и покрыт тестами.
/// </summary>
public static class UpdateChecker
{
    /// <summary>Последний опубликованный релиз нашего продукта.</summary>
    public const string ApiUrl = "https://api.github.com/repos/oover120/KeySwitch-DeepSeek/releases/latest";

    /// <summary>Страница релизов — открывается, если прямой ссылки на файл нет.</summary>
    public const string ReleasesPage = "https://github.com/oover120/KeySwitch-DeepSeek/releases/latest";

    private const string UserAgent = "KeySwitchDeepSeek";

    /// <summary>Версия запущенной сборки (например 1.2.1.0).</summary>
    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);

    /// <summary>Запрашивает последний релиз и сравнивает с <paramref name="current"/>.</summary>
    public static async Task<UpdateInfo> CheckAsync(Version current, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            string json = await client.GetStringAsync(ApiUrl, cancellationToken).ConfigureAwait(false);
            return ReleaseParser.Parse(json, current);
        }
        catch (OperationCanceledException)
        {
            return UpdateInfo.Failure("превышено время ожидания");
        }
        catch (Exception ex)
        {
            return UpdateInfo.Failure(ex.Message);
        }
    }

    /// <summary>Открывает страницу загрузки в браузере по умолчанию.</summary>
    public static void OpenDownloadPage(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Браузер мог быть не настроен — диалог с ссылкой уже показан.
        }
    }
}
