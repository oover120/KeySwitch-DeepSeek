using System.Text.Json.Serialization;
using KeySwitch.Core.Detect;
using KeySwitch.Core.Layout;
using KeySwitch.Core.Learning;
using KeySwitch.Core.Text;

namespace KeySwitch.Data;

/// <summary>Статистика и подтверждённая замена для одного слова и направления.</summary>
public sealed class WordLearningStats
{
    public int UndoCount { get; set; }

    public int AcceptCount { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConfirmedReplacement { get; set; }

    /// <summary>Слово перестало исправляться (порог отмен достигнут).</summary>
    public bool Suppressed { get; set; }

    public DateTime LastSeen { get; set; } = DateTime.Now;

    public WordLearningStats Clone() => (WordLearningStats)MemberwiseClone();
}

/// <summary>Файл обучения целиком (только для JSON-сериализации).</summary>
internal sealed class LearningData
{
    public int Version { get; set; } = 1;

    public Dictionary<string, WordLearningStats> Words { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Самообучение: сколько раз пользователь отменял исправление слова и сколько раз принимал.
/// По достижении порога слово подавляется и (если подключён <see cref="ExceptionStore"/>)
/// добавляется в исключения. Ручные исправления запоминаются отдельно от автоматических.
/// </summary>
public sealed class LearningStore : ILearningStore
{
    private readonly object _lock = new();
    private readonly AppPaths _paths;
    private readonly AppLogger _logger;
    private readonly AppSettings _settings;
    private Dictionary<string, WordLearningStats> _stats = new(StringComparer.OrdinalIgnoreCase);

    public LearningStore(AppPaths paths, AppLogger logger, AppSettings settings, ExceptionStore? exceptions = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Exceptions = exceptions;
    }

    /// <summary>Политика исключений: при подавлении слова оно добавляется туда автоматически.</summary>
    public ExceptionStore? Exceptions { get; set; }

    public int TotalUndos
    {
        get
        {
            lock (_lock)
                return _stats.Values.Sum(stat => stat.UndoCount);
        }
    }

    public int TotalAccepted
    {
        get
        {
            lock (_lock)
                return _stats.Values.Sum(stat => stat.AcceptCount);
        }
    }

    public void Load()
    {
        _paths.EnsureCreated();

        LearningData? data = null;
        if (File.Exists(_paths.LearningFile))
        {
            data = JsonFile.TryRead<LearningData>(_paths.LearningFile, out var error);
            if (data is null)
            {
                var bad = JsonFile.Quarantine(_paths.LearningFile);
                _logger.Warn($"Файл обучения повреждён ({error}), переименован в {Path.GetFileName(bad)}.");
            }
        }

        lock (_lock)
        {
            _stats = new Dictionary<string, WordLearningStats>(StringComparer.OrdinalIgnoreCase);
            if (data?.Words is not null)
            {
                foreach (var pair in data.Words)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
                        continue;
                    _stats[pair.Key] = pair.Value;
                }
            }
        }

        _logger.Info($"Обучение загружено: слов {_stats.Count}, отмен всего {TotalUndos}.");
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                JsonFile.Write(_paths.LearningFile, new LearningData { Words = _stats });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Error("Не удалось сохранить файл обучения.", ex);
            }
        }
    }

    public IReadOnlyDictionary<string, WordLearningStats> Snapshot()
    {
        lock (_lock)
        {
            return _stats.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Clone(),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    public int GetUndoCount(string word, string sourceLayoutId, string targetLayoutId)
    {
        var key = MakeKey(word, sourceLayoutId, targetLayoutId);
        if (key is null)
            return 0;

        lock (_lock)
            return _stats.TryGetValue(key, out var stats) ? stats.UndoCount : 0;
    }

    public bool IsSuppressed(string word, string sourceLayoutId, string targetLayoutId)
    {
        var key = MakeKey(word, sourceLayoutId, targetLayoutId);
        if (key is null)
            return false;

        lock (_lock)
            return _stats.TryGetValue(key, out var stats) && stats.Suppressed;
    }

    public void RecordUndo(string word, string sourceLayoutId, string targetLayoutId, bool keepOriginal = false)
    {
        if (!_settings.LearningEnabled)
            return;

        var key = MakeKey(word, sourceLayoutId, targetLayoutId);
        if (key is null)
            return;

        var threshold = Math.Max(1, _settings.UndoThresholdToSuppress);
        var suppress = false;
        string? direction = null;

        lock (_lock)
        {
            if (!_stats.TryGetValue(key, out var stats))
            {
                stats = new WordLearningStats();
                _stats[key] = stats;
            }

            stats.UndoCount++;
            stats.ConfirmedReplacement = null;
            stats.LastSeen = DateTime.Now;

            if (!stats.Suppressed && (keepOriginal || stats.UndoCount >= threshold))
            {
                stats.Suppressed = true;
                suppress = true;
                direction = ExceptionStore.DirectionFor(
                    LanguageResources.NormalizeLanguage(sourceLayoutId),
                    LanguageResources.NormalizeLanguage(targetLayoutId));
            }
        }

        if (suppress)
        {
            _logger.Info(keepOriginal ? "Пользователь подтвердил исходную раскладку слова." :
                $"Слово подавлено после {threshold} отмен (обучение).");
            if (Exceptions is not null && direction is not null)
                Exceptions.AddWord(word!, direction, fromLearning: true);
        }

        Save();
    }

    public bool RecordManualCorrection(string original, string corrected, string sourceLayoutId, string targetLayoutId)
    {
        if (!_settings.LearningEnabled || !IsLearnablePair(original, corrected, sourceLayoutId, targetLayoutId))
            return false;

        RecordAcceptedWord(original, sourceLayoutId, targetLayoutId, WordText.Normalize(corrected));
        return true;
    }

    public bool TryGetManualCorrection(string word, string sourceLayoutId, string targetLayoutId, out string replacement)
    {
        replacement = string.Empty;
        var key = MakeKey(word, sourceLayoutId, targetLayoutId);
        if (!_settings.LearningEnabled || key == null)
            return false;

        lock (_lock)
        {
            if (!_stats.TryGetValue(key, out var stats) || stats.Suppressed ||
                stats.ConfirmedReplacement is not { Length: > 0 } confirmed ||
                !IsLearnablePair(word, confirmed, sourceLayoutId, targetLayoutId))
            {
                return false;
            }

            replacement = word.Any(char.IsUpper) && !word.Any(char.IsLower)
                ? confirmed.ToUpperInvariant()
                : char.IsUpper(word[0]) ? WordText.CapitalizeFirst(confirmed) : confirmed;
            return true;
        }
    }

    public void RecordAccepted(string word, string sourceLayoutId, string targetLayoutId)
    {
        if (!_settings.LearningEnabled)
            return;

        RecordAcceptedWord(word, sourceLayoutId, targetLayoutId);
    }

    /// <summary>
    /// Ключ записи: "слово|ru→en". Направление нормализуется по языкам раскладок,
    /// поэтому "ru-RU" и "ru" дают одну запись.
    /// </summary>
    public static string? MakeKey(string? word, string? sourceLayoutId, string? targetLayoutId)
    {
        var normalized = (word ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0)
            return null;

        var direction = ExceptionStore.DirectionFor(sourceLayoutId, targetLayoutId);
        return normalized + "|" + direction;
    }

    private static bool IsLearnablePair(string original, string corrected, string sourceLayoutId, string targetLayoutId)
    {
        if (original.Length is 0 or > 64 || corrected.Length is 0 or > 64 ||
            LayoutCatalog.TryGet(sourceLayoutId) is not { } source ||
            LayoutCatalog.TryGet(targetLayoutId) is not { } target || source.Language == target.Language)
        {
            return false;
        }

        var (targetCore, _) = WordText.SplitTrailingPunctuation(corrected);
        Func<char, bool> sourceLetter = source.Language == "ru" ? WordText.IsCyrillicLetter : WordText.IsLatinLetter;
        Func<char, bool> targetLetter = target.Language == "ru" ? WordText.IsCyrillicLetter : WordText.IsLatinLetter;
        return original.Any(sourceLetter) && original.All(ch => sourceLetter(ch) || WordText.IsAmbiguousPunctuation(ch)) &&
               targetCore.Any(targetLetter) && targetCore.All(ch => targetLetter(ch) || ch is '\'' or '’' or '-') &&
               !corrected.Any(char.IsWhiteSpace);
    }

    private void RecordAcceptedWord(string word, string sourceLayoutId, string targetLayoutId, string? confirmed = null)
    {
        var key = MakeKey(word, sourceLayoutId, targetLayoutId);
        if (key is null)
            return;

        lock (_lock)
        {
            if (!_stats.TryGetValue(key, out var stats))
            {
                stats = new WordLearningStats();
                _stats[key] = stats;
            }

            stats.AcceptCount++;
            if (confirmed != null)
            {
                stats.ConfirmedReplacement = confirmed;
                stats.Suppressed = false;
            }
            stats.LastSeen = DateTime.Now;
        }

        Save();
    }
}
