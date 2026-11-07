using System.Diagnostics;
using System.Text;
using System.Text.Json;
using KeySwitch.Core.Detect;
using KeySwitch.Core.Layout;
using KeySwitch.Core.Text;
using KeySwitch.Data;

namespace Evaluate;

/// <summary>
/// Консольный стенд оценки детектора переключения раскладки.
/// Считает полноту (recall) на словах, набранных в неверной раскладке, и
/// ложные срабатывания на корректно набранных словах. Возвращает 0 при PASS и 1 при FAIL.
/// </summary>
internal static class Program
{
    private const double RecallThreshold = 0.98;
    private const double FalsePositiveThreshold = 0.005;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine("Ошибка аргументов: " + ex.Message);
            Console.Error.WriteLine(Options.Usage);
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(Options.Usage);
            return 0;
        }

        if (!File.Exists(options.CorpusPath))
        {
            Console.Error.WriteLine($"Корпус не найден: {options.CorpusPath}");
            return 2;
        }

        var naturalLayout = ResolveLayout(options.Language);
        var typedLayout = ResolveLayout(options.Layout);
        if (naturalLayout.Id == typedLayout.Id)
        {
            Console.Error.WriteLine("--lang и --layout не должны совпадать: переключать нечего.");
            return 2;
        }

        LanguageResources resources;
        try
        {
            using var paths = new TempPaths();
            var logger = new AppLogger(paths.Paths, safeMode: true);
            resources = LanguageResources.Load(logger);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Не удалось загрузить встроенные ассеты: " + ex.Message);
            return 2;
        }

        using (resources)
        {
            if (!resources.IsLoaded)
            {
                Console.Error.WriteLine("Встроенные языковые ассеты недоступны — Evaluate требует собранные Data/Assets.");
                return 2;
            }

            var detector = new LayoutDetector(resources, LayoutPairRegistry.CreateDefault(), new DetectionSettings());
            var report = EvaluateCorpus(options, detector, resources, naturalLayout, typedLayout);
            if (options.Json)
            {
                PrintJson(report, options);
            }
            else
            {
                PrintHuman(report, options);
            }

            return report.Pass ? 0 : 1;
        }
    }

    private static Report EvaluateCorpus(
        Options options,
        LayoutDetector detector,
        ILanguageResourcesProvider resources,
        LayoutTable naturalLayout,
        LayoutTable typedLayout)
    {
        var report = new Report
        {
            Corpus = options.CorpusPath,
            Language = options.Language,
            Layout = options.Layout,
            Reverse = options.Reverse,
        };

        var words = CorpusReader.ReadWords(options.CorpusPath, options.Limit, options.Reverse, typedLayout);
        report.CorpusWords = words.Count;
        if (words.Count == 0)
        {
            report.Errors.Add(new ErrorRow { Word = "(корпус)", Expected = "слова", Actual = "пустой корпус", Reason = "нет слов длиной ≥ 2" });
            report.Pass = false;
            return report;
        }

        var typedDictionary = resources.GetDictionary(typedLayout.Language);
        var naturalDictionary = resources.GetDictionary(naturalLayout.Language);
        var sourceScorer = new LanguageScorer(
            typedDictionary!,
            resources.GetTrigrams(typedLayout.Language)!,
            resources.GetQuadgrams(typedLayout.Language)!,
            detector.Settings);
        var timer = new Stopwatch();
        var caseErrors = new List<ErrorRow>();

        foreach (var typedWord in words)
        {
            var naturalWord = options.Reverse
                ? LayoutConverter.Convert(typedWord, typedLayout, naturalLayout)
                : typedWord;
            var typedText = options.Reverse
                ? typedWord
                : LayoutConverter.Convert(naturalWord, naturalLayout, typedLayout);

            if (naturalWord.Length == 0 || typedText.Length == 0 || naturalWord == typedText)
            {
                report.SkippedShort++;
                continue;
            }

            if (CountLetters(naturalWord) < 2 || CountLetters(typedText) < 2)
            {
                report.SkippedShort++;
                continue;
            }

            if (typedDictionary?.Contains(typedText) == true || sourceScorer.Score(typedText).InDictionary)
            {
                // Набранное «слово» оказалось настоящим словом языка набора — детектор
                // обязан сохранить его (правило «слово есть в словаре исходного языка»).
                report.SkippedAmbiguous++;
                continue;
            }

            report.Cases++;
            var (naturalCore, _) = WordText.SplitTrailingPunctuation(naturalWord);
            var targetKnown = naturalDictionary?.Contains(naturalWord) == true
                              || (naturalCore.Length > 0 && naturalDictionary?.Contains(naturalCore) == true);
            if (targetKnown)
            {
                report.KnownTargetCases++;
            }

            timer.Restart();
            var result = detector.Decide(typedText, typedLayout);
            timer.Stop();
            report.ElapsedTicks += timer.Elapsed.Ticks;
            if (result.Convert)
            {
                report.Confidence[ConfidenceBucket(result.Confidence)]++;
            }
            else
            {
                report.NotConverted++;
            }

            if (result.Convert && string.Equals(result.ConvertedText, naturalWord, StringComparison.Ordinal))
            {
                report.TruePositives++;
                if (targetKnown)
                {
                    report.KnownTargetHits++;
                }
            }
            else if (result.Convert)
            {
                report.WrongConversions++;
                caseErrors.Add(new ErrorRow
                {
                    Word = typedText,
                    Expected = naturalWord,
                    Actual = result.ConvertedText,
                    Reason = result.Reason,
                });
            }
            else
            {
                report.FalseNegatives++;
                caseErrors.Add(new ErrorRow
                {
                    Word = typedText,
                    Expected = naturalWord,
                    Actual = "не переключено",
                    Reason = result.Reason,
                });
            }
        }

        // Ложные срабатывания: корректно набранные слова не должны конвертироваться.
        var correctWords = new List<string>();
        var seenNatural = new HashSet<string>(StringComparer.Ordinal);
        foreach (var typedWord in words)
        {
            var naturalWord = options.Reverse
                ? LayoutConverter.Convert(typedWord, typedLayout, naturalLayout)
                : typedWord;
            if (CountLetters(naturalWord) < 2)
            {
                continue;
            }

            if (seenNatural.Add(naturalWord))
            {
                correctWords.Add(naturalWord);
            }
        }

        foreach (var naturalWord in correctWords)
        {
            report.CorrectWordChecks++;
            timer.Restart();
            var result = detector.Decide(naturalWord, naturalLayout);
            timer.Stop();
            report.ElapsedTicks += timer.Elapsed.Ticks;

            if (result.Convert)
            {
                report.FalsePositives++;
                report.Errors.Add(new ErrorRow
                {
                    Word = naturalWord,
                    Expected = "не переключать",
                    Actual = result.ConvertedText,
                    Reason = result.Reason,
                });
            }
        }

        report.Recall = report.Cases == 0 ? 0.0 : (double)report.TruePositives / report.Cases;
        report.FalsePositiveRate = report.CorrectWordChecks == 0 ? 0.0 : (double)report.FalsePositives / report.CorrectWordChecks;
        report.KnownTargetRecall = report.KnownTargetCases == 0 ? 0.0 : (double)report.KnownTargetHits / report.KnownTargetCases;
        var conversions = report.TruePositives + report.WrongConversions;
        report.Precision = conversions == 0 ? 0.0 : (double)report.TruePositives / conversions;
        report.TotalMilliseconds = report.ElapsedTicks * 1000.0 / Stopwatch.Frequency;
        var processed = report.Cases + report.CorrectWordChecks;
        report.MicrosecondsPerWord = processed == 0 ? 0.0 : report.TotalMilliseconds * 1000.0 / processed;
        report.Pass = report.Recall >= RecallThreshold && report.FalsePositiveRate <= FalsePositiveThreshold;

        report.Errors.AddRange(caseErrors.OrderBy(e => e.Word, StringComparer.Ordinal).Take(50));
        return report;
    }

    private static void PrintHuman(Report report, Options options)
    {
        Console.WriteLine("KeySwitch Evaluate");
        Console.WriteLine(new string('=', 64));
        Console.WriteLine($"Корпус:               {report.Corpus} ({report.CorpusWords} слов)");
        Console.WriteLine($"Язык корпуса:         {report.Language}");
        Console.WriteLine($"Раскладка набора:     {report.Layout}" + (options.Reverse ? " (режим --reverse)" : " (обычный режим)"));
        Console.WriteLine($"Кейсов:               {report.Cases}  (пропущено коротких: {report.SkippedShort}, неоднозначных: {report.SkippedAmbiguous})");
        Console.WriteLine();
        Console.WriteLine($"Верных переключений:  {report.TruePositives}/{report.Cases} = {report.Recall:P2}   (требуется ≥ {RecallThreshold:P0})");
        Console.WriteLine($"  из них цель в словаре: {report.KnownTargetHits}/{report.KnownTargetCases} = {report.KnownTargetRecall:P2} (неизвестная цель — {report.Cases - report.KnownTargetCases})");
        Console.WriteLine($"Не переключено:       {report.FalseNegatives}");
        Console.WriteLine($"Неверно переключено:  {report.WrongConversions}");
        Console.WriteLine($"Точность переключений:{report.Precision,8:P2}");
        Console.WriteLine($"Ложные срабатывания:  {report.FalsePositives}/{report.CorrectWordChecks} = {report.FalsePositiveRate:P2}   (допустимо ≤ {FalsePositiveThreshold:P1})");
        Console.WriteLine($"Время:                {report.TotalMilliseconds:F2} мс, {report.MicrosecondsPerWord:F1} мкс/слово");
        Console.WriteLine();
        Console.WriteLine("Распределение по уверенности (только решения «переключить»):");
        for (var i = 0; i < 5; i++)
        {
            var from = i * 0.2;
            var to = from + 0.2;
            Console.WriteLine($"  {from:F1}–{to:F1}: {report.Confidence[i]}");
        }

        Console.WriteLine($"Не переключено по уверенности: {report.NotConverted}");

        if (report.Errors.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Ошибки (топ-{Math.Min(50, report.Errors.Count)}):");
            foreach (var error in report.Errors.Take(50))
            {
                Console.WriteLine($"  «{error.Word}» → ожидалось «{error.Expected}», факт «{error.Actual}» | {error.Reason}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(report.Pass
            ? "Итог: PASS (recall ≥ 98 %, ложные срабатывания ≤ 0,5 %)"
            : "Итог: FAIL (критерии не выполнены)");
    }

    private static void PrintJson(Report report, Options options)
    {
        var payload = new
        {
            corpus = report.Corpus,
            language = report.Language,
            layout = report.Layout,
            reverse = report.Reverse,
            cases = report.Cases,
            skippedShort = report.SkippedShort,
            skippedAmbiguous = report.SkippedAmbiguous,
            truePositives = report.TruePositives,
            falseNegatives = report.FalseNegatives,
            wrongConversions = report.WrongConversions,
            recall = Math.Round(report.Recall, 6),
            knownTargetCases = report.KnownTargetCases,
            knownTargetHits = report.KnownTargetHits,
            knownTargetRecall = Math.Round(report.KnownTargetRecall, 6),
            correctWordChecks = report.CorrectWordChecks,
            falsePositives = report.FalsePositives,
            falsePositiveRate = Math.Round(report.FalsePositiveRate, 6),
            precision = Math.Round(report.Precision, 6),
            totalMilliseconds = Math.Round(report.TotalMilliseconds, 3),
            microsecondsPerWord = Math.Round(report.MicrosecondsPerWord, 3),
            confidenceHistogram = new[]
            {
                new { from = 0.0, to = 0.2, count = report.Confidence[0] },
                new { from = 0.2, to = 0.4, count = report.Confidence[1] },
                new { from = 0.4, to = 0.6, count = report.Confidence[2] },
                new { from = 0.6, to = 0.8, count = report.Confidence[3] },
                new { from = 0.8, to = 1.0, count = report.Confidence[4] },
            },
            notConvertedByConfidence = report.NotConverted,
            errors = report.Errors.Take(50).Select(e => new
            {
                word = e.Word,
                expected = e.Expected,
                actual = e.Actual,
                reason = e.Reason,
            }),
            criteria = new { recallMin = RecallThreshold, falsePositiveMax = FalsePositiveThreshold },
            pass = report.Pass,
        };

        Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static int ConfidenceBucket(double confidence) => confidence switch
    {
        < 0.2 => 0,
        < 0.4 => 1,
        < 0.6 => 2,
        < 0.8 => 3,
        _ => 4,
    };

    private static int CountLetters(string text)
    {
        var count = 0;
        foreach (var ch in text)
        {
            if (char.IsLetter(ch))
            {
                count++;
            }
        }

        return count;
    }

    private static LayoutTable ResolveLayout(string value) => value switch
    {
        "ru" or "ru-RU" or "ru_RU" => LayoutCatalog.RuRu,
        "en" or "en-US" or "en_US" => LayoutCatalog.EnUs,
        _ => throw new ArgumentException($"неизвестная раскладка «{value}», ожидается ru или en"),
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class Report
    {
        public string Corpus { get; set; } = string.Empty;

        public string Language { get; set; } = "ru";

        public string Layout { get; set; } = "en";

        public bool Reverse { get; set; }

        public int CorpusWords { get; set; }

        public int Cases { get; set; }

        public int SkippedShort { get; set; }

        public int SkippedAmbiguous { get; set; }

        public int TruePositives { get; set; }

        public int KnownTargetCases { get; set; }

        public int KnownTargetHits { get; set; }

        public double KnownTargetRecall { get; set; }

        public int FalseNegatives { get; set; }

        public int WrongConversions { get; set; }

        public int CorrectWordChecks { get; set; }

        public int FalsePositives { get; set; }

        public double Recall { get; set; }

        public double FalsePositiveRate { get; set; }

        public double Precision { get; set; }

        public long ElapsedTicks { get; set; }

        public double TotalMilliseconds { get; set; }

        public double MicrosecondsPerWord { get; set; }

        public int NotConverted { get; set; }

        public int[] Confidence { get; } = new int[5];

        public List<ErrorRow> Errors { get; } = new();

        public bool Pass { get; set; }
    }

    private sealed class ErrorRow
    {
        public string Word { get; set; } = string.Empty;

        public string Expected { get; set; } = string.Empty;

        public string Actual { get; set; } = string.Empty;

        public string Reason { get; set; } = string.Empty;
    }

    private sealed class Options
    {
        public string CorpusPath { get; private set; } = Path.Combine("tools", "Evaluate", "corpus-sample.txt");

        public string Language { get; private set; } = "ru";

        public string Layout { get; private set; } = "en";

        public bool Reverse { get; private set; }

        public bool Json { get; private set; }

        public int Limit { get; private set; } = int.MaxValue;

        public bool ShowHelp { get; private set; }

        public static Options Parse(string[] args)
        {
            var options = new Options();
            var languageSet = false;
            var layoutSet = false;

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "--corpus":
                        options.CorpusPath = RequireValue(args, ref i, arg);
                        break;
                    case "--lang":
                        options.Language = RequireValue(args, ref i, arg).ToLowerInvariant();
                        languageSet = true;
                        break;
                    case "--layout":
                        options.Layout = RequireValue(args, ref i, arg).ToLowerInvariant();
                        layoutSet = true;
                        break;
                    case "--limit":
                        var raw = RequireValue(args, ref i, arg);
                        if (!int.TryParse(raw, out var limit) || limit <= 0)
                        {
                            throw new ArgumentException($"--limit должен быть положительным числом, получено «{raw}»");
                        }

                        options.Limit = limit;
                        break;
                    case "--reverse":
                        options.Reverse = true;
                        break;
                    case "--json":
                        options.Json = true;
                        break;
                    case "--help":
                    case "-h":
                        options.ShowHelp = true;
                        break;
                    default:
                        throw new ArgumentException($"неизвестный аргумент «{arg}»");
                }
            }

            if (options.Language is not ("ru" or "en"))
            {
                throw new ArgumentException($"--lang должен быть ru или en, получено «{options.Language}»");
            }

            if (!layoutSet)
            {
                options.Layout = options.Language == "ru" ? "en" : "ru";
            }

            if (options.Layout is not ("ru" or "en"))
            {
                throw new ArgumentException($"--layout должен быть ru или en, получено «{options.Layout}»");
            }

            if (!languageSet)
            {
                // If only --layout was given, language is the opposite.
                options.Language = options.Layout == "ru" ? "en" : "ru";
            }

            return options;
        }

        public static string Usage =>
            "Использование: dotnet run --project tools/Evaluate -- --corpus <path> [--lang ru|en] [--layout ru|en]\n" +
            "               [--reverse] [--json] [--limit N]\n" +
            "\n" +
            "  --corpus <path>  текстовый корпус (слова через пробел/пунктуацию)\n" +
            "  --lang ru|en     язык корпуса (по умолчанию ru)\n" +
            "  --layout ru|en   раскладка, в которой набран корпус (по умолчанию противоположная --lang)\n" +
            "  --reverse        корпус уже содержит текст в неверной раскладке\n" +
            "  --json           машинный вывод\n" +
            "  --limit N        обработать только первые N слов корпуса\n" +
            "\n" +
            "Критерии PASS: recall ≥ 98 %, ложные срабатывания ≤ 0,5 %. Код возврата 0/1.";

        private static string RequireValue(string[] args, ref int index, string name)
        {
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"после {name} ожидается значение");
            }

            return args[++index];
        }
    }

    private sealed class TempPaths : IDisposable
    {
        private readonly string _root;

        public TempPaths()
        {
            _root = Path.Combine(Path.GetTempPath(), "keyswitch-evaluate-" + Environment.ProcessId);
            Paths = new AppPaths(_root);
        }

        public AppPaths Paths { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Временная папка: не мешает результату.
            }
        }
    }

    private static class CorpusReader
    {
        public static List<string> ReadWords(string path, int limit, bool rawTokens, LayoutTable typedLayout)
        {
            var words = new List<string>();
            var text = File.ReadAllText(path);
            foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (words.Count >= limit)
                {
                    break;
                }

                // В режиме --reverse корпус уже набран в чужой раскладке: символы вроде '[' , ',' , '.'
                // могут быть буквами (х, б, ю), поэтому обрезаем только символы, которых нет в раскладке набора.
                var word = rawTokens ? TrimNotInLayout(token, typedLayout) : TrimNonWordChars(token);
                if (word.Length == 0)
                {
                    continue;
                }

                words.Add(word.ToLowerInvariant());
            }

            return words;
        }

        private static string TrimNotInLayout(string value, LayoutTable typedLayout)
        {
            var start = 0;
            var end = value.Length - 1;
            while (start <= end && !typedLayout.Contains(value[start]))
            {
                start++;
            }

            while (end >= start && !typedLayout.Contains(value[end]))
            {
                end--;
            }

            return start > end ? string.Empty : value[start..(end + 1)];
        }

        private static string TrimNonWordChars(string value)
        {
            var start = 0;
            var end = value.Length - 1;
            while (start <= end && !IsWordChar(value[start]))
            {
                start++;
            }

            while (end >= start && !IsWordChar(value[end]))
            {
                end--;
            }

            return start > end ? string.Empty : value[start..(end + 1)];
        }

        private static bool IsWordChar(char ch)
            => char.IsLetterOrDigit(ch) || ch is '-' or '\'' or '’';
    }
}
