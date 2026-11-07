using System.Diagnostics;

namespace KeySwitch.BuildAssets;

/// <summary>
/// Оркестратор генерации ассетов: скачивание/кэширование источников, словари, ё-пары,
/// n-граммные модели, опечатки и лицензии. Все шаги идемпотентны: повторный запуск
/// перезаписывает ассеты тем же содержимым и не ходит в сеть, если кэш уже заполнен.
/// </summary>
internal static class BuildProgram
{
    /// <summary>Бюджет размера всех ассетов (встроены в сборку, поэтому ограничены).</summary>
    private const long AssetsSizeBudget = 25L * 1024 * 1024;

    private sealed record SourceFile(string FileName, string Url, long MinimumBytes);

    private static readonly SourceFile[] WordSources =
    {
        new("en_US.dic", "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/en/en_US.dic", 400_000),
        new("en_US.aff", "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/en/en_US.aff", 2_000),
        new("ru_RU.dic", "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/ru_RU/ru_RU.dic", 2_000_000),
        new("ru_RU.aff", "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/ru_RU/ru_RU.aff", 40_000),
        new("en_50k.txt", "https://raw.githubusercontent.com/hermitdave/FrequencyWords/master/content/2018/en/en_50k.txt", 400_000),
        new("ru_50k.txt", "https://raw.githubusercontent.com/hermitdave/FrequencyWords/master/content/2018/ru/ru_50k.txt", 500_000),
    };

    private static readonly SourceFile[] NgramSources =
    {
        new("eng_sentences.tsv.bz2", "https://downloads.tatoeba.org/exports/per_language/eng/eng_sentences.tsv.bz2", 15_000_000),
        new("rus_sentences.tsv.bz2", "https://downloads.tatoeba.org/exports/per_language/rus/rus_sentences.tsv.bz2", 10_000_000),
    };

    /// <summary>Тексты лицензий и README: необязательные, при недоступности пишется встроенный текст.</summary>
    private static readonly SourceFile[] AttributeSources =
    {
        new("README_en_US.txt", "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/en/README_en_US.txt", 256),
        new("README_ru_RU.txt", "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/ru_RU/README_ru_RU.txt", 256),
        new("LICENSE_FrequencyWords", "https://raw.githubusercontent.com/hermitdave/FrequencyWords/master/LICENSE", 256),
    };

    public static async Task<int> RunAsync(string[] args)
    {
        var started = Stopwatch.StartNew();

        BuildOptions options;
        try
        {
            options = BuildOptions.Parse(args, FindRepositoryRoot(), AppContext.BaseDirectory, Log);
        }
        catch (ArgumentException ex)
        {
            Log("ОШИБКА: " + ex.Message);
            return 2;
        }

        Log($"BuildAssets: ассеты → {options.OutputDirectory}");
        Log($"BuildAssets: кэш → {options.CacheDirectory}");
        Directory.CreateDirectory(options.OutputDirectory);

        var cache = new SourceCache(options.CacheDirectory);

        try
        {
            TyposData.Write(options.OutputDirectory, Log);

            await EnsureAttributesAsync(cache, options.ForceDownload).ConfigureAwait(false);
            LicenseData.Write(options.OutputDirectory, options.CacheDirectory, Log);

            if (options.SkipWords && options.SkipNgrams)
            {
                Log("словари и n-граммы: пропущены (--skip-words --skip-ngrams).");
            }
            else
            {
                await EnsureWordSourcesAsync(cache, options).ConfigureAwait(false);
                await EnsureNgramSourcesAsync(cache, options).ConfigureAwait(false);

                // Корпус читается один раз: частоты словоформ нужны словарю, счётчики n-грамм — моделям.
                Log("== корпуса ==");
                var corpus = new Dictionary<Language, CorpusAnalysis>
                {
                    [Language.English] = CorpusAnalysis.Analyze(Language.English, cache.PathFor("eng_sentences.tsv.bz2"), Log),
                    [Language.Russian] = CorpusAnalysis.Analyze(Language.Russian, cache.PathFor("rus_sentences.tsv.bz2"), Log),
                };

                if (options.SkipWords)
                    Log("словари: пропущены (--skip-words).");
                else
                    BuildWords(cache, options, corpus);

                if (options.SkipNgrams)
                    Log("n-граммы: пропущены (--skip-ngrams).");
                else
                    BuildNgrams(options, corpus);
            }

            var total = ReportAssets(options.OutputDirectory);
            Log($"итого: {SourceCache.FormatSize(total)} за {started.Elapsed.TotalSeconds:0} с.");

            if (total > AssetsSizeBudget)
            {
                Log($"ВНИМАНИЕ: суммарный размер больше бюджета {SourceCache.FormatSize(AssetsSizeBudget)}. " +
                    "Уменьшите --max-words и/или --max-grams и повторите запуск.");
                return 3;
            }

            return 0;
        }
        catch (Exception ex)
        {
            Log("ОШИБКА: " + ex.Message);
            Log(ex.ToString());
            return 1;
        }
    }

    /// <summary>Словарь ограничивается по-разному: русский — до 400k (морфология), английский — до 200k.</summary>
    private const int EnglishWordLimit = 200_000;

    private static async Task EnsureWordSourcesAsync(SourceCache cache, BuildOptions options)
    {
        foreach (var source in WordSources)
            await cache.EnsureAsync(source.FileName, source.Url, source.MinimumBytes, options.ForceDownload, Log).ConfigureAwait(false);
    }

    private static async Task EnsureNgramSourcesAsync(SourceCache cache, BuildOptions options)
    {
        foreach (var source in NgramSources)
            await cache.EnsureAsync(source.FileName, source.Url, source.MinimumBytes, options.ForceDownload, Log).ConfigureAwait(false);
    }

    private static void BuildWords(SourceCache cache, BuildOptions options, IReadOnlyDictionary<Language, CorpusAnalysis> corpus)
    {
        Log("== словари ==");
        WordListResult? russian = null;

        foreach (var language in new[] { Language.English, Language.Russian })
        {
            var code = language.Code();
            var prefix = language == Language.Russian ? "ru_RU" : "en_US";
            var limit = language == Language.English ? Math.Min(options.MaxWords, EnglishWordLimit) : options.MaxWords;

            var result = WordListBuilder.Build(
                language,
                cache.PathFor(prefix + ".dic"),
                cache.PathFor(prefix + ".aff"),
                cache.PathFor(code + "_50k.txt"),
                corpus[language].WordCounts,
                limit,
                Log);

            var path = Path.Combine(options.OutputDirectory, $"words-{code}.txt.gz");
            WordListBuilder.WriteWordList(path, result.Selected);
            Log($"{code}: words-{code}.txt.gz — {result.Selected.Count:N0} слов, {SourceCache.FormatSize(new FileInfo(path).Length)}.");

            if (language == Language.Russian)
                russian = result;
        }

        if (russian is null)
            return;

        var pairs = WordListBuilder.BuildYoPairs(russian, Language.Russian, options.MaxYoPairs, Log);
        var yoPath = Path.Combine(options.OutputDirectory, "yo-ru.txt.gz");
        WordListBuilder.WriteYoPairs(yoPath, pairs);
        Log($"ru: yo-ru.txt.gz — {pairs.Count:N0} пар, {SourceCache.FormatSize(new FileInfo(yoPath).Length)}.");
    }

    private static void BuildNgrams(BuildOptions options, IReadOnlyDictionary<Language, CorpusAnalysis> corpus)
    {
        Log("== n-граммы ==");
        NgramBuilder.WriteModels(Language.English, corpus[Language.English], options.OutputDirectory, options.MaxGrams, Log);
        NgramBuilder.WriteModels(Language.Russian, corpus[Language.Russian], options.OutputDirectory, options.MaxGrams, Log);
    }

    /// <summary>Лицензии — вспомогательный шаг: недоступный README не должен валить генерацию.</summary>
    private static async Task EnsureAttributesAsync(SourceCache cache, bool force)
    {
        if (!force)
            return;

        foreach (var source in AttributeSources)
            await cache.TryEnsureAsync(source.FileName, source.Url, Log).ConfigureAwait(false);
    }

    private static long ReportAssets(string outputDirectory)
    {
        Log("== состав ассетов ==");
        var files = Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.Length)
            .ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        long total = 0;
        foreach (var file in files)
        {
            total += file.Length;
            var relative = Path.GetRelativePath(outputDirectory, file.FullName);
            Log($"  {relative,-28} {SourceCache.FormatSize(file.Length),10}");
        }

        Log($"  {"всего",-28} {SourceCache.FormatSize(total),10} (бюджет {SourceCache.FormatSize(AssetsSizeBudget)})");
        return total;
    }

    /// <summary>Ищет корень репозитория (каталог с Directory.Build.props и src/KeySwitch.Data).</summary>
    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")) &&
                    Directory.Exists(Path.Combine(directory.FullName, "src", "KeySwitch.Data")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        return Environment.CurrentDirectory;
    }

    private static void Log(string message)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
    }
}
