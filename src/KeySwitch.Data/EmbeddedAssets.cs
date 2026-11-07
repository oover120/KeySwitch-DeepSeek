using System.Reflection;

namespace KeySwitch.Data;

/// <summary>
/// Доступ к встроенным ресурсам <c>KeySwitch.Data.Assets.*</c>. Имена ресурсов зависят от
/// соглашений MSBuild, поэтому ищем по суффиксу и кэшируем карту: приложение не сломается,
/// если ассет переименуют в другую папку, но сохранит простое имя файла.
/// </summary>
internal static class EmbeddedAssets
{
    private const string Prefix = "KeySwitch.Data.Assets.";

    private static readonly Lazy<Dictionary<string, string>> Map = new(BuildMap, isThreadSafe: true);

    public static bool Exists(string fileName) => Map.Value.ContainsKey(fileName);

    public static IReadOnlyCollection<string> Names => Map.Value.Keys;

    /// <summary>Открывает поток ресурса по имени файла (например, "words-en.txt.gz").</summary>
    public static Stream? Open(string fileName)
    {
        if (!Map.Value.TryGetValue(fileName, out var resourceName))
            return null;

        return typeof(EmbeddedAssets).Assembly.GetManifestResourceStream(resourceName);
    }

    /// <summary>Читает ресурс в строку (UTF-8) или null, если ресурса нет.</summary>
    public static string? ReadAllText(string fileName)
    {
        using var stream = Open(fileName);
        if (stream is null)
            return null;

        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static Dictionary<string, string> BuildMap()
    {
        var assembly = typeof(EmbeddedAssets).Assembly;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var trimmed = resourceName[Prefix.Length..];
            var slash = trimmed.LastIndexOf('.'); // имя файла может содержать точки (words-en.txt.gz)
            if (slash <= 0)
                continue;

            // Ресурс с точками вместо разделителей: восстанавливаем имя файла по известным
            // суффиксам: берём часть после последней папки (папки разделены точками).
            var fileName = GuessFileName(trimmed);
            if (fileName is not null)
                map[fileName] = resourceName;
        }

        return map;
    }

    private static string? GuessFileName(string trimmed)
    {
        // Известные расширения ассетов — по ним и восстанавливаем имя файла.
        foreach (var extension in KnownExtensions)
        {
            if (!trimmed.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                continue;

            var withoutExtension = trimmed[..^extension.Length];
            var dot = withoutExtension.LastIndexOf('.');
            return (dot >= 0 ? withoutExtension[(dot + 1)..] : withoutExtension) + extension;
        }

        return null;
    }

    private static readonly string[] KnownExtensions =
    {
        ".txt.gz", ".bin", ".txt", ".json", ".gz",
    };
}
