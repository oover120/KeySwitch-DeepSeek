using System.Text.Json;

namespace KeySwitch.Core.Updates;

/// <summary>
/// Разбор ответа GitHub API «releases/latest». Сети здесь нет — только текст,
/// поэтому логику можно покрыть тестами без интернета.
/// </summary>
public static class ReleaseParser
{
    /// <summary>
    /// Разбирает JSON релиза и сравнивает с <paramref name="current"/>.
    /// Ошибка разбора возвращается в <see cref="UpdateInfo.Error"/>, исключений наружу нет.
    /// </summary>
    public static UpdateInfo Parse(string? json, Version current)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return UpdateInfo.Failure("пустой ответ сервера");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return UpdateInfo.Failure("неожиданный формат ответа");
            }

            string? tag = root.TryGetProperty("tag_name", out JsonElement tagElement)
                ? tagElement.GetString()
                : null;

            Version? latest = ParseTag(tag);
            if (latest is null)
            {
                return UpdateInfo.Failure("не удалось разобрать версию релиза");
            }

            string? releaseUrl = root.TryGetProperty("html_url", out JsonElement htmlElement)
                ? htmlElement.GetString()
                : null;

            string? notes = root.TryGetProperty("body", out JsonElement bodyElement)
                ? bodyElement.GetString()
                : null;

            string? downloadUrl = FindExecutable(root);
            bool hasUpdate = Normalize(latest) > Normalize(current);

            return new UpdateInfo(
                hasUpdate,
                latest.ToString(3),
                downloadUrl ?? releaseUrl,
                releaseUrl,
                TrimNotes(notes),
                null);
        }
        catch (JsonException ex)
        {
            return UpdateInfo.Failure(ex.Message);
        }
    }

    /// <summary>«v1.2.1» → Version(1.2.1); суффиксы вроде «-beta» отбрасываются.</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        ReadOnlySpan<char> span = tag.AsSpan().Trim();
        if (span.Length > 0 && (span[0] == 'v' || span[0] == 'V'))
        {
            span = span[1..];
        }

        int cut = span.IndexOfAny('-', '+', ' ');
        if (cut >= 0)
        {
            span = span[..cut];
        }

        string[] parts = span.ToString().Split('.');
        if (parts.Length is 0 or > 4)
        {
            return null;
        }

        var numbers = new int[4];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out numbers[i]) || numbers[i] < 0)
            {
                return null;
            }
        }

        return new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    /// <summary>Прямая ссылка на первый .exe в ассетах релиза.</summary>
    private static string? FindExecutable(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string? name = asset.TryGetProperty("name", out JsonElement nameElement)
                ? nameElement.GetString()
                : null;

            if (name is null || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? url = asset.TryGetProperty("browser_download_url", out JsonElement urlElement)
                ? urlElement.GetString()
                : null;

            if (!string.IsNullOrEmpty(url))
            {
                return url;
            }
        }

        return null;
    }

    /// <summary>Сравниваем только major.minor.patch: ревизия сборки не влияет.</summary>
    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0));

    private static string? TrimNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return null;
        }

        const int limit = 700;
        string text = notes.Trim();
        return text.Length <= limit ? text : text[..limit] + "…";
    }
}

