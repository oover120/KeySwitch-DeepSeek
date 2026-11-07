using System.Net;
using System.Net.Http.Headers;

namespace KeySwitch.BuildAssets;

/// <summary>
/// Локальный кэш исходных данных. Скачанные файлы лежат в tools/BuildAssets/obj/cache и не попадают
/// в git; повторный запуск не трогает сеть, если файл уже есть. Загрузка атомарная: сначала .tmp,
/// затем переименование, чтобы прерванный запуск не оставил «полуфайл».
/// </summary>
internal sealed class SourceCache
{
    private const int MaxAttempts = 3;
    private static readonly HttpClient Client = CreateClient();

    private readonly string _cacheDirectory;

    public SourceCache(string cacheDirectory)
    {
        _cacheDirectory = cacheDirectory;
        Directory.CreateDirectory(_cacheDirectory);
    }

    public string PathFor(string fileName) => Path.Combine(_cacheDirectory, fileName);

    /// <summary>Возвращает путь к источнику, скачивая его при необходимости.</summary>
    public async Task<string> EnsureAsync(string fileName, string url, long minimumBytes, bool force, Action<string> log)
    {
        var path = PathFor(fileName);
        if (!force && File.Exists(path) && new FileInfo(path).Length >= minimumBytes)
        {
            log($"кэш: {fileName} ({FormatSize(new FileInfo(path).Length)})");
            return path;
        }

        var temporary = path + ".tmp";
        Exception? last = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                log($"скачивание: {url}");
                using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                await using (var target = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    await source.CopyToAsync(target).ConfigureAwait(false);
                }

                var length = new FileInfo(temporary).Length;
                if (length < minimumBytes)
                    throw new IOException($"Файл {fileName} слишком мал: {length} < {minimumBytes} байт.");

                File.Move(temporary, path, overwrite: true);
                log($"скачано: {fileName} ({FormatSize(length)})");
                return path;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                last = ex;
                TryDelete(temporary);
                if (attempt < MaxAttempts)
                {
                    log($"ошибка ({ex.Message}), повтор {attempt}/{MaxAttempts - 1}…");
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt)).ConfigureAwait(false);
                }
            }
        }

        throw new IOException($"Не удалось скачать {url}: {last?.Message}", last);
    }

    /// <summary>Скачивает необязательный файл (лицензия/README): при ошибке возвращает null, сборка продолжается.</summary>
    public async Task<string?> TryEnsureAsync(string fileName, string url, Action<string> log)
    {
        try
        {
            return await EnsureAsync(fileName, url, minimumBytes: 64, force: false, log).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            log($"предупреждение: {fileName} недоступен ({ex.Message}) — будет использован встроенный текст.");
            return null;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KeySwitch-BuildAssets", "1.0"));
        client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        return client;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // не критично: .tmp перезапишется следующим запуском
        }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.0} МБ",
        >= 1024 => $"{bytes / 1024.0:0.0} КБ",
        _ => $"{bytes} Б",
    };
}
