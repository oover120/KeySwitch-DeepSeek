using System.IO.Compression;
using System.Text;

namespace KeySwitch.BuildAssets;

internal enum Language
{
    English,
    Russian,
}

internal static class Languages
{
    public static string Code(this Language language) => language switch
    {
        Language.Russian => "ru",
        _ => "en",
    };

    public static bool IsValidWord(this Language language, string word)
    {
        if (word.Length is < 1 or > 40)
            return false;

        foreach (var ch in word)
        {
            if (language == Language.Russian)
            {
                if (!IsCyrillic(ch))
                    return false;
            }
            else if (!IsLatin(ch))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Кириллица, включая ё/Ё (в нижнем регистре — ё).</summary>
    public static bool IsCyrillic(char ch) => ch is >= 'а' and <= 'я' or 'ё';

    public static bool IsLatin(char ch) => ch is >= 'a' and <= 'z';
}

internal sealed class WordListResult
{
    public WordListResult(
        Dictionary<string, long> selected,
        Dictionary<string, long> vocabulary,
        HashSet<string> dictionaryForms,
        Dictionary<string, long> frequencies,
        Dictionary<string, long> corpusFrequencies,
        int lemmaCount,
        int expandedFormCount)
    {
        Selected = selected;
        Vocabulary = vocabulary;
        DictionaryForms = dictionaryForms;
        Frequencies = frequencies;
        CorpusFrequencies = corpusFrequencies;
        LemmaCount = lemmaCount;
        ExpandedFormCount = expandedFormCount;
    }

    /// <summary>Слова, попавшие в ассет (слово → частота).</summary>
    public Dictionary<string, long> Selected { get; }

    /// <summary>Полный словарь словоформ (до отсечения по лимиту) — нужен для ё-словаря.</summary>
    public Dictionary<string, long> Vocabulary { get; }

    /// <summary>Словоформы, полученные из Hunspell .dic (суффиксы/префиксы).</summary>
    public HashSet<string> DictionaryForms { get; }

    /// <summary>Исходный частотный список FrequencyWords (только реальные частоты).</summary>
    public Dictionary<string, long> Frequencies { get; }

    /// <summary>Частоты словоформ в корпусе Tatoeba (для отбора и проверки ё-пар).</summary>
    public Dictionary<string, long> CorpusFrequencies { get; }

    public int LemmaCount { get; }

    public int ExpandedFormCount { get; }
}

/// <summary>
/// Сборка списка словоформ: Hunspell .dic + аффиксный экспандер + частотный список FrequencyWords
/// + частоты из корпуса Tatoeba. Отбор идёт по «уровням доверия»: слова из частотного списка,
/// затем встреченные в корпусе, затем базовые формы словаря, затем остальные словоформы
/// (внутри уровня — по частоте, при равенстве по алфавиту). Лимит задаётся вызывающим кодом.
/// </summary>
internal static class WordListBuilder
{
    private const int MaxFormsPerLemma = 64;
    private const int MaxCandidateForms = 4_000_000;

    // Уровни приоритета кандидатов (чем меньше, тем раньше слово попадает в ассет).
    private const int TierFrequencyList = 0;
    private const int TierCorpus = 1;
    private const int TierLemma = 2;
    private const int TierForm = 3;

    public static WordListResult Build(
        Language language,
        string dictionaryPath,
        string affixPath,
        string frequencyPath,
        IReadOnlyDictionary<string, int> corpusWordCounts,
        int maxWords,
        Action<string> log)
    {
        var affix = AffixFile.Load(affixPath);
        log($"{language.Code()}: аффиксов SFX {affix.Suffixes.Count}, PFX {affix.Prefixes.Count}, режим флагов {affix.FlagMode}.");

        var entries = HunspellDictionary.Load(dictionaryPath, affix);
        log($"{language.Code()}: лемм в .dic — {entries.Count}.");

        var frequencies = ReadFrequencyList(frequencyPath);
        log($"{language.Code()}: частотный список — {frequencies.Count} слов; корпус — {corpusWordCounts.Count} уникальных слов.");

        var vocabulary = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var dictionaryForms = new HashSet<string>(StringComparer.Ordinal);

        // Статистика окончаний корпуса: нужна, чтобы из огромного хвоста словоформ отбирать
        // не «первые по алфавиту», а формы с типичными для языка окончаниями (ой, ая, ые, ...).
        var suffixCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in corpusWordCounts)
        {
            var word = pair.Key;
            if (word.Length < 3)
                continue;
            var suffix = word[^3..];
            suffixCounts[suffix] = suffixCounts.TryGetValue(suffix, out var count) ? count + pair.Value : pair.Value;
        }

        long SuffixScore(string word)
        {
            if (word.Length < 3)
                return 0;
            return suffixCounts.TryGetValue(word[^3..], out var count) ? count : 0;
        }

        void Offer(string word, long frequency, int tier, bool fromDictionary)
        {
            if (!language.IsValidWord(word))
                return;

            if (fromDictionary)
                dictionaryForms.Add(word);

            var suffixScore = tier == TierForm ? SuffixScore(word) : 0;

            if (vocabulary.TryGetValue(word, out var existing))
            {
                if (tier < existing.Tier || (tier == existing.Tier && (frequency > existing.Frequency
                        || (frequency == existing.Frequency && suffixScore > existing.SuffixScore))))
                {
                    vocabulary[word] = new Candidate(tier, frequency, suffixScore);
                }

                return;
            }

            vocabulary[word] = new Candidate(tier, frequency, suffixScore);
        }

        long CorpusCount(string word) => corpusWordCounts.TryGetValue(word, out var count) ? count : 0;

        // Уровень 0 — слова из частотного списка: у них настоящие счётчики.
        foreach (var pair in frequencies)
            Offer(pair.Key, pair.Value, TierFrequencyList, fromDictionary: false);

        // Леммы с флагами раскрываем в порядке убывания наблюдаемой частоты: при переполнении
        // бюджета редкие леммы остаются только базовой формой (компромисс против взрыва экспандера).
        var orderedEntries = entries
            .Where(entry => language.IsValidWord(entry.Word))
            .OrderByDescending(entry => frequencies.TryGetValue(entry.Word, out var f) ? f : CorpusCount(entry.Word))
            .ThenBy(entry => entry.Word, StringComparer.Ordinal)
            .ToList();

        var expandedForms = 0;
        foreach (var entry in orderedEntries)
        {
            var inFrequencyList = frequencies.TryGetValue(entry.Word, out var listFrequency);
            var corpusLemmaFrequency = CorpusCount(entry.Word);
            var lemmaFrequency = inFrequencyList ? listFrequency : corpusLemmaFrequency;

            var lemmaTier = inFrequencyList ? TierFrequencyList : corpusLemmaFrequency > 0 ? TierCorpus : TierLemma;
            Offer(entry.Word, lemmaFrequency > 0 ? lemmaFrequency : 1, lemmaTier, fromDictionary: true);

            if (!entry.HasFlags || expandedForms >= MaxCandidateForms)
                continue;

            var forms = HunspellDictionary.Expand(entry.Word, entry.Flags, affix, MaxFormsPerLemma);
            foreach (var form in forms)
            {
                if (!language.IsValidWord(form))
                    continue;

                expandedForms++;
                long formFrequency;
                int tier;
                if (frequencies.TryGetValue(form, out var direct))
                {
                    formFrequency = direct;
                    tier = TierFrequencyList;
                }
                else
                {
                    var corpusFrequency = CorpusCount(form);
                    if (corpusFrequency > 0)
                    {
                        formFrequency = corpusFrequency;
                        tier = TierCorpus;
                    }
                    else
                    {
                        // Наследуем частоту леммы, чтобы словоформы частых слов шли раньше
                        // словоформ редких (а не в алфавитном порядке).
                        formFrequency = lemmaFrequency > 0 ? Math.Max(1, lemmaFrequency / 20) : 1;
                        tier = TierForm;
                    }
                }

                Offer(form, formFrequency, tier, fromDictionary: true);

                if (expandedForms >= MaxCandidateForms)
                    break;
            }
        }

        var tierCounts = new int[4];
        foreach (var candidate in vocabulary.Values)
            tierCounts[candidate.Tier]++;

        log($"{language.Code()}: кандидатов {vocabulary.Count:N0} (частотный список {tierCounts[TierFrequencyList]:N0}, "
            + $"корпус {tierCounts[TierCorpus]:N0}, леммы {tierCounts[TierLemma]:N0}, словоформы {tierCounts[TierForm]:N0}); "
            + $"словоформ из словаря {dictionaryForms.Count:N0}, развёрнуто {expandedForms:N0}.");

        // Отбор: уровень → частота ↓ → правдоподобность окончания ↓ → алфавит. Так в лимит сначала
        // попадают реальные частотные слова и встреченные в корпусе формы, затем все леммы,
        // а из хвоста словоформ — формы с типичными окончаниями, а не только начало алфавита.
        var ordered = vocabulary.ToList();
        ordered.Sort(static (a, b) =>
        {
            var byTier = a.Value.Tier.CompareTo(b.Value.Tier);
            if (byTier != 0)
                return byTier;

            var byFrequency = b.Value.Frequency.CompareTo(a.Value.Frequency);
            if (byFrequency != 0)
                return byFrequency;

            var bySuffix = b.Value.SuffixScore.CompareTo(a.Value.SuffixScore);
            return bySuffix != 0 ? bySuffix : string.CompareOrdinal(a.Key, b.Key);
        });

        var selected = new Dictionary<string, long>(Math.Min(maxWords, ordered.Count), StringComparer.Ordinal);
        var selectedTiers = new int[4];
        foreach (var pair in ordered)
        {
            if (selected.Count >= maxWords)
                break;
            selected[pair.Key] = pair.Value.Frequency;
            selectedTiers[pair.Value.Tier]++;
        }

        if (ordered.Count > maxWords)
        {
            var cut = ordered[maxWords - 1].Value;
            log($"{language.Code()}: лимит {maxWords:N0} слов, отсечение на частоте {cut.Frequency} (уровень {cut.Tier}).");
        }

        log($"{language.Code()}: в ассет вошло {selected.Count:N0} слов (частотный список {selectedTiers[TierFrequencyList]:N0}, "
            + $"корпус {selectedTiers[TierCorpus]:N0}, леммы {selectedTiers[TierLemma]:N0}, словоформы {selectedTiers[TierForm]:N0}).");
        var vocabularyFrequencies = new Dictionary<string, long>(vocabulary.Count, StringComparer.Ordinal);
        foreach (var pair in vocabulary)
            vocabularyFrequencies[pair.Key] = pair.Value.Frequency;

        var corpusFrequencies = new Dictionary<string, long>(corpusWordCounts.Count, StringComparer.Ordinal);
        foreach (var pair in corpusWordCounts)
            corpusFrequencies[pair.Key] = pair.Value;

        return new WordListResult(
            selected,
            vocabularyFrequencies,
            dictionaryForms,
            frequencies,
            corpusFrequencies,
            entries.Count,
            expandedForms);
    }

    /// <summary>Кандидат в словарь: уровень приоритета, частота и правдоподобность окончания.</summary>
    private readonly struct Candidate
    {
        public Candidate(int tier, long frequency, long suffixScore = 0)
        {
            Tier = tier;
            Frequency = frequency;
            SuffixScore = suffixScore;
        }

        public int Tier { get; }

        public long Frequency { get; }

        public long SuffixScore { get; }
    }

    /// <summary>Записывает words-*.txt.gz: строки «слово\tчастота», UTF-8 без BOM.</summary>
    public static void WriteWordList(string path, Dictionary<string, long> words)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
        using var writer = new StreamWriter(gzip, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        foreach (var pair in words.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            writer.Write(pair.Key + "\t" + pair.Value + "\n");
    }

    /// <summary>
    /// Строит пары «слово с е» → «слово с ё» для однозначных случаев. Строгое правило
    /// «е-варианта нет в словаре» неприменимо: в ru_RU.dic у каждой ё-словоформы есть е-вариант
    /// (он же словоформа других лемм), поэтому используется частотная проверка по двум источникам
    /// (FrequencyWords + корпус): пара отбрасывается, если е-вариант употребительнее ё-варианта —
    /// это признак самостоятельного слова («все»/«всё», «узнаем»/«узнаём»). Для заведомо
    /// безопасных частых пар-написаний («еще», «ее») сделано исключение. Не больше maxPairs пар.
    /// </summary>
    public static List<KeyValuePair<string, string>> BuildYoPairs(
        WordListResult result,
        Language language,
        int maxPairs,
        Action<string> log)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        if (language != Language.Russian)
            return pairs;

        // Пары, где е-вариант — то же самое слово в другом написании, а не омоним.
        var whitelist = new HashSet<string>(StringComparer.Ordinal) { "еще", "ее" };

        long ObservedFrequency(string word)
        {
            var list = result.Frequencies.TryGetValue(word, out var f) ? f : 0;
            var corpus = result.CorpusFrequencies.TryGetValue(word, out var c) ? c : 0;
            return Math.Max(list, corpus);
        }

        var ordered = result.DictionaryForms
            .Where(word => word.Contains('ё'))
            .Select(word => (Word: word, Frequency: ObservedFrequency(word)))
            .OrderByDescending(item => item.Frequency)
            .ThenBy(item => item.Word, StringComparer.Ordinal);

        var skippedAmbiguous = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (word, _) in ordered)
        {
            var withoutYo = word.Replace('ё', 'е');
            if (withoutYo == word || withoutYo.Length == 0)
                continue;
            if (!result.DictionaryForms.Contains(withoutYo))
                continue; // е-вариант не является словоформой словаря — нечего проверять

            var yoFrequency = ObservedFrequency(word);
            var eFrequency = ObservedFrequency(withoutYo);
            if (!whitelist.Contains(withoutYo) && eFrequency > 0 && yoFrequency < eFrequency)
            {
                skippedAmbiguous++;
                continue; // е-вариант употребительнее — скорее всего, это другое слово
            }

            if (!seen.Add(withoutYo))
                continue;

            pairs.Add(new KeyValuePair<string, string>(withoutYo, word));
            if (pairs.Count >= maxPairs)
                break;
        }

        log($"ru: ё-пар {pairs.Count}, пропущено неоднозначных {skippedAmbiguous} (из {result.DictionaryForms.Count} словоформ словаря).");
        return pairs;
    }

    /// <summary>Записывает yo-ru.txt.gz: строки «слово_с_е\tслово_с_ё».</summary>
    public static void WriteYoPairs(string path, IEnumerable<KeyValuePair<string, string>> pairs)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
        using var writer = new StreamWriter(gzip, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        foreach (var pair in pairs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            writer.Write(pair.Key + "\t" + pair.Value + "\n");
    }

    private static Dictionary<string, long> ReadFrequencyList(string path)
    {
        var frequencies = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var rawLine in File.ReadLines(path, Encoding.UTF8))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            var space = line.IndexOf(' ');
            string word;
            long frequency = 1;
            if (space < 0)
            {
                word = line.ToLowerInvariant();
            }
            else
            {
                word = line[..space].ToLowerInvariant();
                _ = long.TryParse(line[(space + 1)..].Trim(), out frequency);
                if (frequency <= 0)
                    frequency = 1;
            }

            if (word.Length == 0)
                continue;

            if (!frequencies.TryGetValue(word, out var current) || current < frequency)
                frequencies[word] = frequency;
        }

        return frequencies;
    }
}
