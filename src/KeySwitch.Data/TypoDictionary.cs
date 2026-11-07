using System.Text;
using KeySwitch.Core.PostProcess;

namespace KeySwitch.Data;

/// <summary>
/// Редактируемый словарь опечаток. Встроенные списки лежат ассетами и при первом запуске
/// выкладываются в пользовательские файлы <see cref="AppPaths.TyposRuFile"/> и
/// <see cref="AppPaths.TyposEnFile"/>; дальше приложение читает именно их.
/// Формат строки: <c>неверно=верно</c>, строки-комментарии начинаются с '#',
/// регистр неверного слова приводится к нижнему.
/// </summary>
public sealed class TypoDictionary : ITypoDictionary
{
    private const string HeaderRu =
        "# KeySwitch: словарь опечаток (русский). Формат: неверно=верно. Неверное слово — нижним регистром.";

    private const string HeaderEn =
        "# KeySwitch: словарь опечаток (английский). Формат: неверно=верно. Неверное слово — нижним регистром.";

    private readonly object _lock = new();
    private readonly AppPaths _paths;
    private readonly AppLogger _logger;
    private readonly Dictionary<string, string> _ru = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _en = new(StringComparer.OrdinalIgnoreCase);

    public TypoDictionary(AppPaths paths, AppLogger logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Reload();
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                var keys = new HashSet<string>(_ru.Keys, StringComparer.OrdinalIgnoreCase);
                keys.UnionWith(_en.Keys);
                return keys.Count;
            }
        }
    }

    /// <summary>Последняя ошибка чтения/записи (null — всё хорошо).</summary>
    public string? LastError { get; private set; }

    /// <summary>Пары русского словаря (неверно → верно).</summary>
    public IReadOnlyDictionary<string, string> RuEntries
    {
        get
        {
            lock (_lock)
                return new Dictionary<string, string>(_ru, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Пары английского словаря (неверно → верно).</summary>
    public IReadOnlyDictionary<string, string> EnEntries
    {
        get
        {
            lock (_lock)
                return new Dictionary<string, string>(_en, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Перечитывает оба файла, при необходимости выкладывая встроенные дефолты.</summary>
    public void Reload()
    {
        _paths.EnsureCreated();
        LastError = null;

        EnsureDefaultFile(_paths.TyposRuFile, "typos-ru.txt", HeaderRu);
        EnsureDefaultFile(_paths.TyposEnFile, "typos-en.txt", HeaderEn);

        LoadFile(_paths.TyposRuFile, _ru);
        LoadFile(_paths.TyposEnFile, _en);

        _logger.Info($"Словарь опечаток: ru {_ru.Count}, en {_en.Count}.");
    }

    public bool TryGetReplacement(string word, out string replacement)
    {
        replacement = string.Empty;
        if (string.IsNullOrWhiteSpace(word))
            return false;

        var normalized = word.Trim().ToLowerInvariant();
        lock (_lock)
        {
            if (_ru.TryGetValue(normalized, out var russian))
            {
                replacement = russian;
                return true;
            }

            if (_en.TryGetValue(normalized, out var english))
            {
                replacement = english;
                return true;
            }
        }

        replacement = string.Empty;
        return false;
    }

    /// <summary>Добавляет/заменяет пару в памяти; для записи на диск вызови <see cref="Save"/>.</summary>
    public void SetEntry(string language, string wrong, string right)
    {
        var (dictionary, _) = SelectLanguage(language);
        if (dictionary is null)
            return;

        var wrongNormalized = (wrong ?? string.Empty).Trim().ToLowerInvariant();
        var rightNormalized = (right ?? string.Empty).Trim();
        if (wrongNormalized.Length == 0 || rightNormalized.Length == 0)
            return;

        lock (_lock)
            dictionary[wrongNormalized] = rightNormalized;
    }

    /// <summary>Удаляет пару из памяти; для записи на диск вызови <see cref="Save"/>.</summary>
    public bool RemoveEntry(string language, string wrong)
    {
        var (dictionary, _) = SelectLanguage(language);
        if (dictionary is null)
            return false;

        var wrongNormalized = (wrong ?? string.Empty).Trim().ToLowerInvariant();
        lock (_lock)
            return dictionary.Remove(wrongNormalized);
    }

    /// <summary>Записывает пользовательские файлы (атомарно, отсортированные пары).</summary>
    public void Save()
    {
        try
        {
            Dictionary<string, string> ru;
            Dictionary<string, string> en;
            lock (_lock)
            {
                ru = new Dictionary<string, string>(_ru, StringComparer.OrdinalIgnoreCase);
                en = new Dictionary<string, string>(_en, StringComparer.OrdinalIgnoreCase);
            }

            WriteFile(_paths.TyposRuFile, HeaderRu, ru);
            WriteFile(_paths.TyposEnFile, HeaderEn, en);
            LastError = null;
            _logger.Info($"Словарь опечаток сохранён: ru {ru.Count}, en {en.Count}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = ex.Message;
            _logger.Error("Не удалось сохранить словарь опечаток.", ex);
        }
    }

    /// <summary>Возвращает встроенные дефолты и перезагружает словарь.</summary>
    public void ResetToDefaults()
    {
        WriteEmbeddedDefault(_paths.TyposRuFile, "typos-ru.txt");
        WriteEmbeddedDefault(_paths.TyposEnFile, "typos-en.txt");
        Reload();
    }

    private (Dictionary<string, string>? Dictionary, string Language) SelectLanguage(string language) =>
        LanguageResources.NormalizeLanguage(language) switch
        {
            LanguageResources.Russian => (_ru, LanguageResources.Russian),
            LanguageResources.English => (_en, LanguageResources.English),
            _ => (null, string.Empty),
        };

    private void EnsureDefaultFile(string path, string assetName, string header)
    {
        if (File.Exists(path))
            return;

        WriteEmbeddedDefault(path, assetName, header);
    }

    private void WriteEmbeddedDefault(string path, string assetName, string? header = null)
    {
        try
        {
            var content = EmbeddedAssets.ReadAllText(assetName);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            if (content is null)
            {
                content = (header ?? string.Empty) + Environment.NewLine;
                _logger.Warn($"Встроенный файл {assetName} не найден, создаю пустой словарь.");
            }

            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = ex.Message;
            _logger.Error($"Не удалось создать файл словаря опечаток {Path.GetFileName(path)}.", ex);
        }
    }

    private void LoadFile(string path, Dictionary<string, string> target)
    {
        target.Clear();
        if (!File.Exists(path))
            return;

        try
        {
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] == '#')
                    continue;

                var separator = trimmed.IndexOf('=');
                if (separator <= 0)
                    continue;

                var wrong = trimmed[..separator].Trim().ToLowerInvariant();
                var right = trimmed[(separator + 1)..].Trim();
                if (wrong.Length == 0 || right.Length == 0)
                    continue;

                target[wrong] = right;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = ex.Message;
            _logger.Error($"Не удалось прочитать файл словаря опечаток {Path.GetFileName(path)}.", ex);
        }
    }

    private static void WriteFile(string path, string header, Dictionary<string, string> entries)
    {
        var builder = new StringBuilder();
        builder.AppendLine(header);

        foreach (var pair in entries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            builder.Append(pair.Key).Append('=').AppendLine(pair.Value);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
