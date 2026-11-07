using System.Text;

namespace KeySwitch.BuildAssets;

/// <summary>Параметры запуска генератора ассетов.</summary>
internal sealed class BuildOptions
{
    public const int DefaultMaxWords = 400_000;
    public const int DefaultMaxGrams = 400_000;
    public const int DefaultMaxYoPairs = 30_000;

    public string OutputDirectory { get; private set; } = string.Empty;

    public string CacheDirectory { get; private set; } = string.Empty;

    public bool ForceDownload { get; private set; }

    public bool SkipWords { get; private set; }

    public bool SkipNgrams { get; private set; }

    public int MaxWords { get; private set; } = DefaultMaxWords;

    public int MaxGrams { get; private set; } = DefaultMaxGrams;

    public int MaxYoPairs { get; private set; } = DefaultMaxYoPairs;

    public static BuildOptions Parse(string[] args, string repositoryRoot, string? projectDirectory, Action<string> log)
    {
        var options = new BuildOptions
        {
            OutputDirectory = Path.Combine(repositoryRoot, "src", "KeySwitch.Data", "Assets"),
            CacheDirectory = Path.Combine(repositoryRoot, "tools", "BuildAssets", "obj", "cache"),
        };

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--out":
                    options.OutputDirectory = RequireValue(args, ref i, arg);
                    break;
                case "--cache":
                    options.CacheDirectory = RequireValue(args, ref i, arg);
                    break;
                case "--max-words":
                    options.MaxWords = ParsePositive(RequireValue(args, ref i, arg), arg);
                    break;
                case "--max-grams":
                    options.MaxGrams = ParsePositive(RequireValue(args, ref i, arg), arg);
                    break;
                case "--force":
                case "--force-download":
                    options.ForceDownload = true;
                    break;
                case "--skip-words":
                    options.SkipWords = true;
                    break;
                case "--skip-ngrams":
                    options.SkipNgrams = true;
                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Неизвестный аргумент: {arg}. Используйте --help.");
            }
        }

        options.OutputDirectory = Path.GetFullPath(options.OutputDirectory);
        options.CacheDirectory = Path.GetFullPath(options.CacheDirectory);
        return options;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            BuildAssets — генератор встроенных ассетов KeySwitch.Data.

            Использование: dotnet run --project tools/BuildAssets -- [параметры]

              --out <каталог>    каталог ассетов (по умолчанию src/KeySwitch.Data/Assets)
              --cache <каталог>  каталог кэша скачанных источников (по умолчанию tools/BuildAssets/obj/cache)
              --max-words <n>    максимум слов (ru до 400000, en до 200000)
              --max-grams <n>    максимум n-грамм на модель (по умолчанию 400000)
              --force            перекачать источники, даже если они уже в кэше
              --skip-words       не пересобирать словари (words-*.txt.gz, yo-ru.txt.gz)
              --skip-ngrams      не пересобирать n-граммные модели
              --help             эта справка

            Повторный запуск идемпотентен: результат детерминирован, источники берутся из кэша.
            """);
    }

    private static string RequireValue(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"Для {name} нужно значение.");

        return args[++index];
    }

    private static int ParsePositive(string value, string name)
    {
        if (!int.TryParse(value, out var result) || result <= 0)
            throw new ArgumentException($"Для {name} нужно положительное целое, получено: {value}.");

        return result;
    }

    /// <summary>Пишет текст в файл UTF-8 без BOM (и создаёт каталог).</summary>
    public static void WriteText(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
