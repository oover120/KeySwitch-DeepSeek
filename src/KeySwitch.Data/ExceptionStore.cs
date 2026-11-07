using KeySwitch.Core.Detect;
using KeySwitch.Core.Learning;
using KeySwitch.Core.Text;

namespace KeySwitch.Data;

/// <summary>Режим обработки окна процесса.</summary>
public enum ExceptionMode
{
    /// <summary>Не трогать приложение вообще.</summary>
    Disabled = 0,

    /// <summary>Только ручные горячие клавиши, автопереключение выключено.</summary>
    ManualOnly = 1,

    /// <summary>Полная обработка.</summary>
    Full = 2,
}

/// <summary>Правило для процесса: имя исполняемого файла и режим обработки.</summary>
public sealed class ProcessRule
{
    public string ProcessName { get; set; } = string.Empty;

    public ExceptionMode Mode { get; set; } = ExceptionMode.Full;

    public ProcessRule()
    {
    }

    public ProcessRule(string processName, ExceptionMode mode)
    {
        ProcessName = processName;
        Mode = mode;
    }
}

/// <summary>
/// Слово-исключение: не исправлять в указанном направлении переключения.
/// Направления — "ru→en", "en→ru" или "both".
/// </summary>
public sealed class WordException
{
    public string Word { get; set; } = string.Empty;

    public string Direction { get; set; } = ExceptionStore.DirectionBoth;

    /// <summary>Добавлено автоматически по статистике обучения, а не пользователем.</summary>
    public bool FromLearning { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.Now;
}

/// <summary>Файл исключений целиком (используется только для JSON-сериализации).</summary>
internal sealed class ExceptionData
{
    public int Version { get; set; } = 1;

    public List<ProcessRule> Processes { get; set; } = new();

    public List<WordException> Words { get; set; } = new();
}

/// <summary>
/// Пользовательские исключения: процессы (режим обработки), глобальное правило папки Games
/// и слова с направлением переключения. Проверки вызываются из хука, поэтому горячий путь
/// <see cref="CheckWord"/> работает по подготовленным HashSet'ам под коротким локом.
/// </summary>
public sealed class ExceptionStore : IExceptionPolicy
{
    public const string DirectionRuEn = "ru→en";
    public const string DirectionEnRu = "en→ru";
    public const string DirectionBoth = "both";

    /// <summary>Фрагмент пути, при котором окно безусловно исключается (игровая папка).</summary>
    public const string GamesFolderMarker = "\\Games\\";

    private const int FrequentWordsTop = 100;

    private readonly object _lock = new();
    private readonly AppPaths _paths;
    private readonly AppLogger _logger;
    private readonly HashSet<string> _wordLookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ExceptionMode> _processLookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _topWords = new(StringComparer.OrdinalIgnoreCase);
    private LanguageResources? _resources;

    public ExceptionStore(AppPaths paths, AppLogger logger, LanguageResources? resources)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _resources = resources;
        Processes = new List<ProcessRule>();
        Words = new List<WordException>();
    }

    /// <summary>Список правил процессов; после прямых правок вызови <see cref="Save"/>.</summary>
    public List<ProcessRule> Processes { get; }

    /// <summary>Список слов-исключений; после прямых правок вызови <see cref="Save"/>.</summary>
    public List<WordException> Words { get; }

    /// <summary>
    /// Ресурсы нужны только для проверки «топ-100 частотных слов». Если их ещё нет
    /// (создание на старте до загрузки словарей), проверка отключается — см. docs/notes/data.md.
    /// </summary>
    public void AttachResources(LanguageResources? resources)
    {
        lock (_lock)
        {
            _resources = resources;
            _topWords.Clear();
        }
    }

    /// <summary>
    /// Дефолтные процессы. Редакторы и терминалы — ManualOnly: там пользователь часто
    /// работает в двух языках и автоподмена мешает, но ручная конвертация нужна.
    /// Удалённый рабочий стол (mstsc) — Full: раскладка локальная и автопереключение полезно.
    /// </summary>
    public static IReadOnlyList<ProcessRule> CreateDefaultProcesses() => new List<ProcessRule>
    {
        new("Code.exe", ExceptionMode.ManualOnly),
        new("Cursor.exe", ExceptionMode.ManualOnly),
        new("devenv.exe", ExceptionMode.ManualOnly),
        new("idea64.exe", ExceptionMode.ManualOnly),
        new("pycharm64.exe", ExceptionMode.ManualOnly),
        new("WindowsTerminal.exe", ExceptionMode.ManualOnly),
        new("cmd.exe", ExceptionMode.ManualOnly),
        new("powershell.exe", ExceptionMode.ManualOnly),
        new("pwsh.exe", ExceptionMode.ManualOnly),
        new("mstsc.exe", ExceptionMode.Full),
    };

    public void Load()
    {
        _paths.EnsureCreated();

        ExceptionData? data = null;
        if (File.Exists(_paths.ExceptionsFile))
        {
            data = JsonFile.TryRead<ExceptionData>(_paths.ExceptionsFile, out var error);
            if (data is null)
            {
                var bad = JsonFile.Quarantine(_paths.ExceptionsFile);
                _logger.Warn($"Файл исключений повреждён ({error}), переименован в {Path.GetFileName(bad)}.");
            }
        }

        if (data is null)
        {
            data = new ExceptionData
            {
                Processes = CreateDefaultProcesses().ToList(),
                Words = new List<WordException>(),
            };
        }

        lock (_lock)
        {
            Processes.Clear();
            Words.Clear();

            foreach (var rule in data.Processes)
            {
                if (string.IsNullOrWhiteSpace(rule.ProcessName))
                    continue;
                Processes.Add(new ProcessRule(rule.ProcessName.Trim(), rule.Mode));
            }

            foreach (var word in data.Words)
            {
                if (string.IsNullOrWhiteSpace(word.Word))
                    continue;
                word.Word = word.Word.Trim().ToLowerInvariant();
                word.Direction = NormalizeDirection(word.Direction);
                Words.Add(word);
            }

            RebuildIndexesLocked();
        }

        _logger.Info($"Исключения загружены: процессов {Processes.Count}, слов {Words.Count}.");
        WarmFrequentWords();

        // Первый запуск — сохраняем дефолты, чтобы пользователь видел редактируемый файл.
        if (!File.Exists(_paths.ExceptionsFile))
            TrySave();
    }

    public void Save()
    {
        ExceptionData data;
        lock (_lock)
        {
            RebuildIndexesLocked();
            data = new ExceptionData
            {
                Processes = Processes.Select(rule => new ProcessRule(rule.ProcessName, rule.Mode)).ToList(),
                Words = Words.Select(word => new WordException
                {
                    Word = word.Word,
                    Direction = word.Direction,
                    FromLearning = word.FromLearning,
                    AddedAt = word.AddedAt,
                }).ToList(),
            };
        }

        try
        {
            JsonFile.Write(_paths.ExceptionsFile, data);
            _logger.Debug("Исключения сохранены.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error("Не удалось сохранить файл исключений.", ex);
        }
    }

    public WordVerdict CheckWord(string word, string sourceLayoutId, string targetLayoutId)
    {
        var (core, _) = WordText.SplitTrailingPunctuation(WordText.Normalize(word ?? string.Empty));
        core = core.Trim();
        if (core.Length == 0)
            return WordVerdict.NotAWord;

        var hasLatin = false;
        var hasCyrillic = false;
        var letters = 0;
        foreach (var ch in core)
        {
            if (char.IsDigit(ch))
                return WordVerdict.NotAWord;

            if (ch is '-' or '\'' or '’')
                continue;

            if (!char.IsLetter(ch))
                return WordVerdict.NotAWord;

            letters++;
            if (WordText.IsLatinLetter(ch))
                hasLatin = true;
            else if (WordText.IsCyrillicLetter(ch))
                hasCyrillic = true;
            else
                return WordVerdict.NotAWord;
        }

        if (letters < 2 || core.Length < 2)
            return WordVerdict.TooShort;

        if (hasLatin && hasCyrillic)
            return WordVerdict.NotAWord;

        var source = LanguageResources.NormalizeLanguage(sourceLayoutId);
        var target = LanguageResources.NormalizeLanguage(targetLayoutId);
        var direction = DirectionFor(source, target);

        lock (_lock)
        {
            if (_wordLookup.Contains(WordKey(core, direction)) || _wordLookup.Contains(WordKey(core, DirectionBoth)))
                return WordVerdict.BlockedByUser;
        }

        if (IsFrequentWord(core, source) || IsFrequentWord(core, target))
            return WordVerdict.TooFrequent;

        return WordVerdict.Allow;
    }

    public bool CanAddWordException(string word, out string error)
    {
        error = string.Empty;
        var normalized = (word ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            error = "Слово не задано.";
            return false;
        }

        var letters = 0;
        var hasLatin = false;
        var hasCyrillic = false;
        foreach (var ch in normalized)
        {
            if (char.IsDigit(ch))
            {
                error = "Числа и слова с цифрами не добавляются в исключения.";
                return false;
            }

            if (ch is '-' or '\'' or '’')
                continue;

            if (!char.IsLetter(ch))
            {
                error = "Слово должно состоять только из букв.";
                return false;
            }

            letters++;
            if (WordText.IsLatinLetter(ch))
                hasLatin = true;
            else if (WordText.IsCyrillicLetter(ch))
                hasCyrillic = true;
            else
            {
                error = "Поддерживаются только русские и английские буквы.";
                return false;
            }
        }

        if (letters < 2)
        {
            error = "Слишком короткое слово: нужно минимум 2 буквы.";
            return false;
        }

        if (hasLatin && hasCyrillic)
        {
            error = "Слово не должно смешивать русские и английские буквы.";
            return false;
        }

        if (IsFrequentWord(normalized, hasCyrillic ? LanguageResources.Russian : LanguageResources.English))
        {
            error = "Это слово входит в топ-100 частотных — его нельзя исключать.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Добавляет слово-исключение. Валидацию (<see cref="CanAddWordException"/>) выполняет
    /// вызывающая сторона: обучение добавляет слова в обход ограничения «топ-100».
    /// </summary>
    public void AddWord(string word, string direction, bool fromLearning = false)
    {
        var normalized = (word ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0)
            return;

        var normalizedDirection = NormalizeDirection(direction);

        lock (_lock)
        {
            var existing = Words.FirstOrDefault(item =>
                string.Equals(item.Word, normalized, StringComparison.OrdinalIgnoreCase)
                && string.Equals(NormalizeDirection(item.Direction), normalizedDirection, StringComparison.Ordinal));

            if (existing is not null)
            {
                existing.FromLearning |= fromLearning;
                RebuildIndexesLocked();
            }
            else
            {
                Words.Add(new WordException
                {
                    Word = normalized,
                    Direction = normalizedDirection,
                    FromLearning = fromLearning,
                    AddedAt = DateTime.Now,
                });
                RebuildIndexesLocked();
            }
        }

        _logger.Info($"Слово-исключение добавлено ({normalizedDirection}{(fromLearning ? ", обучение" : string.Empty)}).");
        Save();
    }

    public void RemoveWord(WordException item)
    {
        if (item is null)
            return;

        lock (_lock)
        {
            Words.RemoveAll(word => ReferenceEquals(word, item)
                                    || (string.Equals(word.Word, item.Word, StringComparison.OrdinalIgnoreCase)
                                        && string.Equals(NormalizeDirection(word.Direction), NormalizeDirection(item.Direction), StringComparison.Ordinal)));
            RebuildIndexesLocked();
        }

        Save();
    }

    public ExceptionMode GetMode(string processName, string processPath)
    {
        if (IsGamesFolder(processPath))
            return ExceptionMode.Disabled;

        var name = NormalizeProcessName(processName);
        if (name.Length == 0)
            return ExceptionMode.Full;

        lock (_lock)
        {
            if (_processLookup.TryGetValue(name, out var mode))
                return mode;

            // Имя без ".exe" тоже принимаем.
            var withoutExtension = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? name[..^4]
                : name + ".exe";

            if (_processLookup.TryGetValue(withoutExtension, out mode))
                return mode;
        }

        return ExceptionMode.Full;
    }

    public void SetMode(string processName, ExceptionMode mode)
    {
        var name = NormalizeProcessName(processName);
        if (name.Length == 0)
            return;

        lock (_lock)
        {
            var existing = Processes.FirstOrDefault(rule =>
                string.Equals(NormalizeProcessName(rule.ProcessName), name, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                existing.Mode = mode;
            }
            else
            {
                Processes.Add(new ProcessRule(name, mode));
            }

            RebuildIndexesLocked();
        }

        Save();
    }

    /// <summary>Глобальное правило игровой папки: путь содержит "\Games\" (любой регистр).</summary>
    public static bool IsGamesFolder(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
            return false;

        var path = processPath.Replace('/', '\\');
        return path.Contains(GamesFolderMarker, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Нормализует направление: принимает "ru->en"/"ru→en"/"RU-EN", "both", пустое → both.</summary>
    public static string NormalizeDirection(string? direction)
    {
        if (string.IsNullOrWhiteSpace(direction))
            return DirectionBoth;

        var value = direction.Trim().ToLowerInvariant()
            .Replace("->", "→")
            .Replace(" ", string.Empty);

        return value switch
        {
            "ru→en" or "ru-en" or "ru_en" or "ruen" => DirectionRuEn,
            "en→ru" or "en-ru" or "en_ru" or "enru" => DirectionEnRu,
            "both" or "any" or "все" or "оба" => DirectionBoth,
            _ => DirectionBoth,
        };
    }

    /// <summary>Направление по идентификаторам раскладок ("ru-RU", "en-US") → "ru→en".</summary>
    public static string DirectionFor(string? sourceLanguage, string? targetLanguage)
    {
        var source = LanguageResources.NormalizeLanguage(sourceLanguage);
        var target = LanguageResources.NormalizeLanguage(targetLanguage);
        if (source is null || target is null)
            return DirectionBoth;

        return (source, target) switch
        {
            (LanguageResources.Russian, LanguageResources.English) => DirectionRuEn,
            (LanguageResources.English, LanguageResources.Russian) => DirectionEnRu,
            _ => DirectionBoth,
        };
    }

    internal bool IsFrequentWord(string word, string? language)
    {
        var code = LanguageResources.NormalizeLanguage(language);
        if (code is null)
            return false;

        lock (_lock)
        {
            if (_topWords.TryGetValue(code, out var cached))
                return cached.Contains(word);
        }

        var resources = _resources;
        if (resources is null)
            return false;

        var built = new HashSet<string>(
            resources.GetMostFrequentWords(code, FrequentWordsTop),
            StringComparer.OrdinalIgnoreCase);

        lock (_lock)
        {
            _topWords[code] = built;
        }

        return built.Contains(word);
    }

    /// <summary>Прогрев проверки «топ-100» — чтобы первый вызов из хука не тратил время на сортировку.</summary>
    public void WarmFrequentWords()
    {
        if (_resources is null)
            return;

        foreach (var language in new[] { LanguageResources.Russian, LanguageResources.English })
        {
            var top = new HashSet<string>(
                _resources.GetMostFrequentWords(language, FrequentWordsTop),
                StringComparer.OrdinalIgnoreCase);

            lock (_lock)
            {
                _topWords[language] = top;
            }
        }
    }

    private void RebuildIndexesLocked()
    {
        _wordLookup.Clear();
        _processLookup.Clear();

        foreach (var word in Words)
        {
            var normalized = (word.Word ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized.Length == 0)
                continue;

            word.Word = normalized;
            word.Direction = NormalizeDirection(word.Direction);
            _wordLookup.Add(WordKey(normalized, word.Direction));
        }

        foreach (var rule in Processes)
        {
            var name = NormalizeProcessName(rule.ProcessName);
            if (name.Length == 0)
                continue;

            rule.ProcessName = name;
            _processLookup[name] = rule.Mode;
        }
    }

    private static string WordKey(string word, string direction) => word + "\u0001" + direction;

    private static string NormalizeProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return string.Empty;

        var value = processName.Trim();
        if (value.Contains('\\') || value.Contains('/'))
        {
            var slash = value.LastIndexOfAny(new[] { '\\', '/' });
            value = value[(slash + 1)..];
        }

        return value;
    }

    private void TrySave()
    {
        try
        {
            Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error("Не удалось сохранить файл исключений по умолчанию.", ex);
        }
    }
}
