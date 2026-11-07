using System.IO;
using KeySwitch.Core.Detect;
using KeySwitch.Core.Layout;
using KeySwitch.Core.Text;

namespace KeySwitch.Tests.TestSupport;

/// <summary>
/// Словарь на основе <see cref="HashSet{T}"/> — точная in-memory реализация
/// <see cref="IWordDictionary"/> для тестов детектора, без слоя Data.
/// </summary>
public sealed class InMemoryWordDictionary : IWordDictionary
{
    private readonly HashSet<string> _words = new(StringComparer.Ordinal);

    public InMemoryWordDictionary(string language, IEnumerable<string> words)
    {
        Language = language;
        var frequency = 1;
        foreach (var word in words)
        {
            var normalized = WordText.Normalize(word.Trim());
            if (normalized.Length == 0)
                continue;
            if (_words.Add(normalized))
                frequency++;
        }

        Count = _words.Count;
    }

    public string Language { get; }

    public int Count { get; }

    public bool Contains(string word) => !string.IsNullOrEmpty(word) && _words.Contains(WordText.Normalize(word));

    public int GetFrequency(string word)
    {
        if (string.IsNullOrEmpty(word))
            return -1;
        return _words.Contains(WordText.Normalize(word)) ? 1 : -1;
    }
}

/// <summary>
/// Символьная n-граммная модель, обученная прямо в памяти на списке слов.
/// Сглаживание Лапласа: P(gram) = (count + 0.5) / (total + 0.5 * (distinct + 500)).
/// Модель специально простая: тесты проверяют логику детектора, а не качество данных.
/// </summary>
public sealed class InMemoryNgramModel : ICharNgramModel
{
    private readonly Dictionary<ulong, int> _counts = new();
    private readonly double _denominator;
    private readonly double _unknownLog10;

    public InMemoryNgramModel(string language, int order, IEnumerable<string> words)
    {
        Language = language;
        Order = order;

        var total = 0;
        foreach (var word in words)
        {
            NgramHasher.ForEachGram(word, order, hash =>
            {
                _counts.TryGetValue(hash, out var count);
                _counts[hash] = count + 1;
                total++;
            });
        }

        GramCount = _counts.Count;
        _denominator = total + 0.5 * (_counts.Count + 500.0);
        _unknownLog10 = _denominator > 0 ? Math.Log10(0.5 / _denominator) : -6.0;
    }

    public string Language { get; }

    public int Order { get; }

    public int GramCount { get; }

    public double GetLogProbHash(ulong hash)
    {
        if (_counts.TryGetValue(hash, out var count))
            return Math.Log10((count + 0.5) / _denominator);
        return _unknownLog10;
    }

    public bool HasGramHash(ulong hash) => _counts.ContainsKey(hash);
}

/// <summary>Провайдер языковых ресурсов для тестов: словари и n-граммы из TestData.</summary>
public sealed class TestLanguageResources : ILanguageResourcesProvider
{
    private readonly Dictionary<string, IWordDictionary> _dictionaries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ICharNgramModel> _trigrams = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ICharNgramModel> _quadgrams = new(StringComparer.OrdinalIgnoreCase);

    public TestLanguageResources(IReadOnlyList<string> ruWords, IReadOnlyList<string> enWords)
    {
        RuWords = ruWords;
        EnWords = enWords;

        _dictionaries["ru"] = new InMemoryWordDictionary("ru", ruWords);
        _dictionaries["en"] = new InMemoryWordDictionary("en", enWords);
        _trigrams["ru"] = new InMemoryNgramModel("ru", 3, ruWords);
        _trigrams["en"] = new InMemoryNgramModel("en", 3, enWords);
        _quadgrams["ru"] = new InMemoryNgramModel("ru", 4, ruWords);
        _quadgrams["en"] = new InMemoryNgramModel("en", 4, enWords);
    }

    public IReadOnlyList<string> RuWords { get; }

    public IReadOnlyList<string> EnWords { get; }

    public IWordDictionary? GetDictionary(string language) => _dictionaries.GetValueOrDefault(language);

    public ICharNgramModel? GetTrigrams(string language) => _trigrams.GetValueOrDefault(language);

    public ICharNgramModel? GetQuadgrams(string language) => _quadgrams.GetValueOrDefault(language);
}

/// <summary>Загрузка списков слов и общая среда для тестов детектора.</summary>
public static class TestEnvironment
{
    private static readonly Lazy<TestLanguageResources> ResourcesLazy = new(() =>
        new TestLanguageResources(LoadWords("words-ru.txt"), LoadWords("words-en.txt")));

    private static readonly Lazy<LayoutPairRegistry> PairsLazy = new(LayoutPairRegistry.CreateDefault);

    public static TestLanguageResources Resources => ResourcesLazy.Value;

    public static LayoutPairRegistry Pairs => PairsLazy.Value;

    public static DetectionSettings DefaultSettings() => new();

    /// <summary>Детектор на тестовых ресурсах с переданными настройками (по умолчанию — стандартными).</summary>
    public static LayoutDetector CreateDetector(DetectionSettings? settings = null)
        => new(Resources, Pairs, settings ?? new DetectionSettings());

    public static LayoutTable En => LayoutCatalog.EnUs;

    public static LayoutTable Ru => LayoutCatalog.RuRu;

    public static IReadOnlyList<string> LoadWords(string fileName)
    {
        var path = ResolveTestDataPath(fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Не найден тестовый список слов: {path}", path);

        var words = new List<string>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            words.Add(line);
        }

        if (words.Count == 0)
            throw new InvalidDataException($"Пустой тестовый список слов: {path}");

        return words;
    }

    public static string ResolveTestDataPath(string fileName)
    {
        var fromOutput = Path.Combine(AppContext.BaseDirectory, "TestData", fileName);
        if (File.Exists(fromOutput))
            return fromOutput;

        // Фолбэк на запуск из IDE/каталога проекта.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "KeySwitch.Tests", "TestData", fileName);
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        return fromOutput;
    }
}
