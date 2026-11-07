using KeySwitch.Core.Detect;

namespace KeySwitch.Data;

/// <summary>
/// Встроенные языковые ресурсы: словари словоформ и символьные 3/4-граммные модели,
/// вшитые в сборку. Загрузка ленивая и потокобезопасная: ресурс конкретного языка
/// читается при первом обращении и кэшируется; <see cref="IsLoaded"/> становится true,
/// когда доступны все шесть ассетов.
/// </summary>
public sealed class LanguageResources : ILanguageResourcesProvider, IDisposable
{
    public const string English = "en";
    public const string Russian = "ru";

    private static readonly string[] SupportedLanguages = { English, Russian };

    private readonly AppLogger _logger;
    private readonly object _lock = new();
    private readonly Dictionary<string, WordDictionary?> _dictionaries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CharNgramModel?> _trigrams = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CharNgramModel?> _quadgrams = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    private LanguageResources(AppLogger logger) => _logger = logger;

    /// <summary>Читает встроенные ассеты (по умолчанию — все четыре модели и два словаря).</summary>
    public static LanguageResources Load(AppLogger logger)
    {
        var resources = new LanguageResources(logger ?? throw new ArgumentNullException(nameof(logger)));
        resources.LoadAll();
        return resources;
    }

    /// <summary>true, когда успешно загружены все языки и заказанные модели.</summary>
    public bool IsLoaded { get; private set; }

    public IWordDictionary? GetDictionary(string language)
    {
        var code = NormalizeLanguage(language);
        if (code is null)
            return null;

        lock (_lock)
        {
            if (_dictionaries.TryGetValue(code, out var cached))
                return cached;

            var dictionary = LoadDictionary(code);
            _dictionaries[code] = dictionary;
            return dictionary;
        }
    }

    public ICharNgramModel? GetTrigrams(string language) => GetModel(trigrams: true, language);

    public ICharNgramModel? GetQuadgrams(string language) => GetModel(trigrams: false, language);

    /// <summary>Самые частые слова языка (нужны политике исключений).</summary>
    public IReadOnlyList<string> GetMostFrequentWords(string language, int count)
    {
        var dictionary = GetDictionary(language) as WordDictionary;
        return dictionary?.GetMostFrequent(count) ?? Array.Empty<string>();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _dictionaries.Clear();
            _trigrams.Clear();
            _quadgrams.Clear();
        }
    }

    /// <summary>Нормализует идентификаторы "ru-RU", "ru_RU", "RU", "en-US" → "ru"/"en".</summary>
    public static string? NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return null;

        var value = language.Trim().ToLowerInvariant();
        var separator = value.IndexOfAny(new[] { '-', '_' });
        if (separator > 0)
            value = value[..separator];

        return value switch
        {
            "ru" or "russian" => Russian,
            "en" or "english" => English,
            _ => null,
        };
    }

    private void LoadAll()
    {
        var all = true;
        foreach (var language in SupportedLanguages)
        {
            all &= GetDictionary(language) is not null;
            all &= GetTrigrams(language) is not null;
            all &= GetQuadgrams(language) is not null;
        }

        IsLoaded = all;
        if (all)
            _logger.Info("Встроенные языковые ресурсы загружены.");
        else
            _logger.Warn("Часть встроенных языковых ресурсов недоступна — автопереключение будет работать хуже.");
    }

    private ICharNgramModel? GetModel(bool trigrams, string language)
    {
        var code = NormalizeLanguage(language);
        if (code is null)
            return null;

        var cache = trigrams ? _trigrams : _quadgrams;
        lock (_lock)
        {
            if (cache.TryGetValue(code, out var cached))
                return cached;

            var model = LoadModel(code, trigrams ? 3 : 4);
            cache[code] = model;
            return model;
        }
    }

    private WordDictionary? LoadDictionary(string language)
    {
        if (_disposed)
            return null;

        var fileName = $"words-{language}.txt.gz";
        try
        {
            using var stream = EmbeddedAssets.Open(fileName);
            if (stream is null)
            {
                _logger.Warn($"Встроенный словарь {fileName} не найден.");
                return null;
            }

            var dictionary = WordDictionary.ReadGzip(stream, language);
            _logger.Debug($"Словарь {language}: {dictionary.Count} словоформ.");
            return dictionary;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _logger.Error($"Не удалось загрузить словарь {fileName}.", ex);
            return null;
        }
    }

    private CharNgramModel? LoadModel(string language, int order)
    {
        if (_disposed)
            return null;

        var fileName = $"ngrams-{language}-{order}.bin";
        try
        {
            using var stream = EmbeddedAssets.Open(fileName);
            if (stream is null)
            {
                _logger.Warn($"Встроенная модель {fileName} не найдена.");
                return null;
            }

            var model = CharNgramModel.Read(stream, language);
            _logger.Debug($"Модель {language}/{order}: {model.GramCount} n-грамм.");
            return model;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _logger.Error($"Не удалось загрузить модель {fileName}.", ex);
            return null;
        }
    }
}
