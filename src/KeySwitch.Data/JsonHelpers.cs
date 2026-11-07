using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeySwitch.Data;

/// <summary>
/// Единые настройки JSON для всех файлов KeySwitch: camelCase, enum'ы строками,
/// терпимость к регистру и BOM-less UTF-8. Чтение и запись идут только через этот класс,
/// чтобы формат не разъезжался между настройками, исключениями и обучением.
/// </summary>
public static class JsonFile
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static T? Read<T>(string path) where T : class
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return JsonSerializer.Deserialize<T>(stream, Options);
    }

    /// <summary>
    /// Атомарная запись: сначала во временный файл в той же папке, затем подмена.
    /// Так падение процесса посреди записи не оставляет обрезанный JSON.
    /// </summary>
    public static void Write<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, Options);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
        {
            try
            {
                File.Replace(temp, path, destinationBackupFileName: null);
                return;
            }
            catch (IOException)
            {
                // File.Replace требует прав и не любит занятый файл — пробуем перенос.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Безопасное чтение: null, если файла нет или он повреждён; причина — в <paramref name="error"/>.</summary>
    public static T? TryRead<T>(string path, out string? error) where T : class
    {
        error = null;
        if (!File.Exists(path))
            return null;

        try
        {
            return Read<T>(path);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Переименовывает повреждённый файл в <c>*.bad</c>, чтобы он не мешал дефолту.</summary>
    public static string Quarantine(string path)
    {
        var bad = path + ".bad";
        try
        {
            File.Move(path, bad, overwrite: true);
            return bad;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, Options);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
