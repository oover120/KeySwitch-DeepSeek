using System.IO;
using System.Runtime.CompilerServices;
using KeySwitch.Core.Detect;
using KeySwitch.Core.Layout;
using KeySwitch.Core.Text;
using KeySwitch.Tests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace KeySwitch.Tests;

public class DetectorTests
{
    private readonly ITestOutputHelper _output;

    public DetectorTests(ITestOutputHelper output) => _output = output;

    private static readonly LayoutDetector Detector = TestEnvironment.CreateDetector();

    private static IReadOnlyList<string> RuWords => TestEnvironment.Resources.RuWords;

    private static IReadOnlyList<string> EnWords => TestEnvironment.Resources.EnWords;

    [Fact]
    public void RussianWords_TypedInEnLayout_AreDetected()
    {
        var (total, detected, mismatches) = MeasureRecall(RuWords, TestEnvironment.Ru, TestEnvironment.En, Detector);
        var recall = total == 0 ? 0.0 : (double)detected / total;
        _output.WriteLine($"RU→EN: {detected}/{total} = {recall:P2}; пропущено {mismatches.Count}");
        foreach (var mismatch in mismatches.Take(50))
        {
            _output.WriteLine("  " + mismatch);
        }

        Assert.True(total >= 300, $"Слишком мало слов для метрики: {total}");
        Assert.True(
            recall >= 0.98,
            $"Recall RU→EN = {recall:P2} < 98 % ({detected}/{total}). Примеры: {string.Join("; ", mismatches.Take(20))}");
    }

    [Fact]
    public void EnglishWords_TypedInRuLayout_AreDetected()
    {
        var (total, detected, mismatches) = MeasureRecall(EnWords, TestEnvironment.En, TestEnvironment.Ru, Detector);
        var recall = total == 0 ? 0.0 : (double)detected / total;
        _output.WriteLine($"EN→RU: {detected}/{total} = {recall:P2}; пропущено {mismatches.Count}");
        foreach (var mismatch in mismatches.Take(50))
        {
            _output.WriteLine("  " + mismatch);
        }

        Assert.True(total >= 300, $"Слишком мало слов для метрики: {total}");
        Assert.True(
            recall >= 0.98,
            $"Recall EN→RU = {recall:P2} < 98 % ({detected}/{total}). Примеры: {string.Join("; ", mismatches.Take(20))}");
    }

    [Fact]
    public void CorrectRussianWords_AreNotConverted()
    {
        var falsePositives = new List<string>();
        foreach (var word in RuWords)
        {
            var result = Detector.Decide(word, TestEnvironment.Ru);
            if (result.Convert)
            {
                falsePositives.Add($"{word} → '{result.ConvertedText}' ({result.Reason})");
            }
        }

        _output.WriteLine($"Ложных срабатываний на корректных RU: {falsePositives.Count} из {RuWords.Count}");
        foreach (var item in falsePositives.Take(50))
        {
            _output.WriteLine("  " + item);
        }

        Assert.Empty(falsePositives);
    }

    [Fact]
    public void CorrectEnglishWords_AreNotConverted()
    {
        var falsePositives = new List<string>();
        foreach (var word in EnWords)
        {
            var result = Detector.Decide(word, TestEnvironment.En);
            if (result.Convert)
            {
                falsePositives.Add($"{word} → '{result.ConvertedText}' ({result.Reason})");
            }
        }

        _output.WriteLine($"Ложных срабатываний на корректных EN: {falsePositives.Count} из {EnWords.Count}");
        foreach (var item in falsePositives.Take(50))
        {
            _output.WriteLine("  " + item);
        }

        Assert.Empty(falsePositives);
    }

    [Fact]
    public void UnknownRussianWordsFromCorpus_AreNotConverted()
    {
        var words = LoadCorpusWords("corpus-sample.txt");
        if (words.Count == 0)
        {
            _output.WriteLine("corpus-sample.txt не найден — тест пропущен (вернитесь после создания корпуса)");
            return;
        }

        var dictionary = TestEnvironment.Resources.GetDictionary("ru")!;
        var unknown = words.Where(w => w.Length >= 2 && !dictionary.Contains(w)).ToList();
        var falsePositives = new List<string>();
        foreach (var word in unknown)
        {
            var result = Detector.Decide(word, TestEnvironment.Ru);
            if (result.Convert)
            {
                falsePositives.Add($"{word} → '{result.ConvertedText}' ({result.Reason})");
            }
        }

        var ratio = unknown.Count == 0 ? 0.0 : (double)falsePositives.Count / unknown.Count;
        _output.WriteLine($"Неизвестных RU-слов: {unknown.Count}; ложных срабатываний: {falsePositives.Count} ({ratio:P2})");
        foreach (var item in falsePositives.Take(50))
        {
            _output.WriteLine("  " + item);
        }

        Assert.True(unknown.Count >= 50, $"Слишком мало неизвестных слов в корпусе: {unknown.Count}");
        Assert.True(ratio <= 0.005, $"FP на неизвестных RU-словах = {ratio:P2} > 0,5 % ({falsePositives.Count}/{unknown.Count})");
    }

    [Fact]
    public void UnknownEnglishWordsFromCorpus_AreNotConverted()
    {
        var words = LoadCorpusWords("corpus-sample-en.txt");
        if (words.Count == 0)
        {
            _output.WriteLine("corpus-sample-en.txt не найден — тест пропущен (вернитесь после создания корпуса)");
            return;
        }

        var dictionary = TestEnvironment.Resources.GetDictionary("en")!;
        var unknown = words.Where(w => w.Length >= 2 && !dictionary.Contains(w)).ToList();
        var falsePositives = new List<string>();
        foreach (var word in unknown)
        {
            var result = Detector.Decide(word, TestEnvironment.En);
            if (result.Convert)
            {
                falsePositives.Add($"{word} → '{result.ConvertedText}' ({result.Reason})");
            }
        }

        var ratio = unknown.Count == 0 ? 0.0 : (double)falsePositives.Count / unknown.Count;
        _output.WriteLine($"Неизвестных EN-слов: {unknown.Count}; ложных срабатываний: {falsePositives.Count} ({ratio:P2})");
        foreach (var item in falsePositives.Take(50))
        {
            _output.WriteLine("  " + item);
        }

        Assert.True(unknown.Count >= 50, $"Слишком мало неизвестных слов в корпусе: {unknown.Count}");
        Assert.True(ratio <= 0.005, $"FP на неизвестных EN-словах = {ratio:P2} > 0,5 % ({falsePositives.Count}/{unknown.Count})");
    }

    [Fact]
    public void WordInSourceDictionary_IsKept()
    {
        var ru = Detector.Decide("привет", TestEnvironment.Ru);
        Assert.False(ru.Convert);
        Assert.Equal("слово есть в словаре исходного языка", ru.Reason);

        var en = Detector.Decide("hello", TestEnvironment.En);
        Assert.False(en.Convert);
        Assert.Equal("слово есть в словаре исходного языка", en.Reason);
    }

    [Fact]
    public void WordInTargetDictionaryOnly_IsConverted()
    {
        var en = Detector.Decide("ghbdtn", TestEnvironment.En);
        Assert.True(en.Convert);
        Assert.Equal("привет", en.ConvertedText);
        Assert.Equal("en-US", en.SourceLayoutId);
        Assert.Equal("ru-RU", en.TargetLayoutId);
        Assert.False(en.SourceInDictionary);
        Assert.True(en.TargetInDictionary);
        Assert.InRange(en.Confidence, 0.0, 1.0);
        Assert.Contains("Δ=", en.Reason);
        Assert.Contains("порог", en.Reason);

        var ru = Detector.Decide("руддщ", TestEnvironment.Ru);
        Assert.True(ru.Convert);
        Assert.Equal("hello", ru.ConvertedText);
    }

    [Fact]
    public void CaseIsPreservedInConversion()
    {
        var result = Detector.Decide("Ghbdtn", TestEnvironment.En);
        Assert.True(result.Convert);
        Assert.Equal("Привет", result.ConvertedText);
    }

    [Fact]
    public void TrailingPunctuation_IsPreserved()
    {
        var comma = Detector.Decide("ghbdtn?", TestEnvironment.En);
        Assert.True(comma.Convert);
        Assert.Equal("привет,", comma.ConvertedText);

        var exclamation = Detector.Decide("ghbdtn!", TestEnvironment.En);
        Assert.True(exclamation.Convert);
        Assert.Equal("привет!", exclamation.ConvertedText);

        var period = Detector.Decide("ghbdtn/", TestEnvironment.En);
        Assert.True(period.Convert);
        Assert.Equal("привет.", period.ConvertedText);
    }

    [Fact]
    public void TooShortWord_IsKept()
    {
        var result = Detector.Decide("g", TestEnvironment.En);
        Assert.False(result.Convert);
        Assert.Equal("слово слишком короткое", result.Reason);

        var withDigit = Detector.Decide("g1", TestEnvironment.En);
        Assert.False(withDigit.Convert);
        Assert.Equal("слово слишком короткое", withDigit.Reason);
    }

    [Fact]
    public void TextWithoutConvertibleCharacters_IsKept()
    {
        var result = Detector.Decide("привет", TestEnvironment.En);
        Assert.False(result.Convert);
        Assert.Equal("символы не конвертируются", result.Reason);
    }

    [Fact]
    public void DetectorWithoutPairs_KeepsWord()
    {
        var detector = new LayoutDetector(TestEnvironment.Resources, new LayoutPairRegistry(), TestEnvironment.DefaultSettings());
        var result = detector.Decide("ghbdtn", TestEnvironment.En);
        Assert.False(result.Convert);
        Assert.Equal("нет пары раскладок", result.Reason);
        Assert.Null(detector.Pairs.GetAlternate(TestEnvironment.En));
    }

    [Fact]
    public void DetectorWithoutModels_KeepsWord()
    {
        var detector = new LayoutDetector(new NoModelsProvider(), TestEnvironment.Pairs, TestEnvironment.DefaultSettings());
        var result = detector.Decide("ghbdtn", TestEnvironment.En);
        Assert.False(result.Convert);
        Assert.Equal("нет языковых моделей", result.Reason);
    }

    [Fact]
    public void Threshold_RequiresEnoughDeltaForNonDictionaryCandidate()
    {
        var settings = TestEnvironment.DefaultSettings();
        var detector = TestEnvironment.CreateDetector(settings);

        // "прива" отсутствует в словаре, поэтому порог умножается на 2.5 и слово не переключается.
        var result = detector.Decide("ghbdf", TestEnvironment.En);
        Assert.False(result.Convert);
        Assert.False(result.TargetInDictionary);
    }

    [Fact]
    public void EarlySwitch_WithHighThreshold_DoesNotConvert()
    {
        var settings = TestEnvironment.DefaultSettings();
        settings.EarlySwitchEnabled = true;
        settings.EarlySwitchThreshold = 20.0;
        var detector = TestEnvironment.CreateDetector(settings);

        var result = detector.Decide("ghbdtn", TestEnvironment.En, TestEnvironment.Ru, early: true);
        Assert.False(result.Convert);
        Assert.True(result.TargetInDictionary);
    }

    [Fact]
    public void EarlySwitch_WithLoweredThreshold_ConvertsDictionaryWord()
    {
        var settings = TestEnvironment.DefaultSettings();
        settings.EarlySwitchEnabled = true;
        settings.EarlySwitchThreshold = 3.0;
        var detector = TestEnvironment.CreateDetector(settings);

        var result = detector.Decide("ghbdtn", TestEnvironment.En, TestEnvironment.Ru, early: true);
        Assert.True(result.Convert);
        Assert.Equal("привет", result.ConvertedText);
    }

    [Fact]
    public void EarlySwitch_NonDictionaryCandidate_IsKept()
    {
        var settings = TestEnvironment.DefaultSettings();
        settings.EarlySwitchEnabled = true;
        settings.EarlySwitchMinLength = 3;
        settings.EarlySwitchThreshold = 1.0;
        var detector = TestEnvironment.CreateDetector(settings);

        var result = detector.Decide("ghb", TestEnvironment.En, TestEnvironment.Ru, early: true);
        Assert.False(result.Convert);
        Assert.Equal("раннее переключение требует словарного слова", result.Reason);
    }

    [Fact]
    public void DefaultSettings_MatchContract()
    {
        var settings = new DetectionSettings();
        Assert.Equal(2, settings.MinWordLength);
        Assert.Equal(2.0, settings.ConfidenceThreshold);
        Assert.Equal(2.5, settings.NoDictionaryThresholdMultiplier);
        Assert.Equal(3.0, settings.DictionaryBonus);
        Assert.Equal(1.5, settings.UnknownWordPenalty);
        Assert.Equal(1.0, settings.NgramWeight);
        Assert.Equal(8.0, settings.ImpossiblePenalty);
        Assert.False(settings.EarlySwitchEnabled);
        Assert.Equal(4, settings.EarlySwitchMinLength);
        Assert.Equal(6.0, settings.EarlySwitchThreshold);
        Assert.True(settings.UseFrequencyWeighting);
    }

    [Fact]
    public void DefaultSettings_Clone_IsIndependent()
    {
        var settings = new DetectionSettings();
        var clone = settings.Clone();
        clone.ConfidenceThreshold = 10.0;
        clone.EarlySwitchEnabled = true;

        Assert.Equal(2.0, settings.ConfidenceThreshold);
        Assert.False(settings.EarlySwitchEnabled);
        Assert.Equal(10.0, clone.ConfidenceThreshold);
    }

    [Fact]
    public void Pairs_AlternateBothWays()
    {
        Assert.Same(TestEnvironment.Ru, TestEnvironment.Pairs.GetAlternate(TestEnvironment.En));
        Assert.Same(TestEnvironment.En, TestEnvironment.Pairs.GetAlternate(TestEnvironment.Ru));
        Assert.Single(TestEnvironment.Pairs.GetAlternates(TestEnvironment.En));
    }

    private static (int Total, int Detected, List<string> Mismatches) MeasureRecall(
        IReadOnlyList<string> words,
        LayoutTable natural,
        LayoutTable typed,
        LayoutDetector detector)
    {
        var typedDictionary = TestEnvironment.Resources.GetDictionary(typed.Language)!;
        var mismatches = new List<string>();
        var total = 0;
        var detected = 0;

        foreach (var word in words)
        {
            var wrong = LayoutConverter.Convert(word, natural, typed);
            var letters = wrong.Count(char.IsLetter);
            if (letters < 2)
            {
                // Слово короче порога детектора — нечего проверять.
                continue;
            }

            if (typedDictionary.Contains(wrong))
            {
                // Набранное слово случайно является настоящим словом чужого языка — детектор обязан его сохранить.
                continue;
            }

            total++;
            var result = detector.Decide(wrong, typed);
            if (result.Convert && result.ConvertedText == word)
            {
                detected++;
            }
            else
            {
                mismatches.Add($"{word} → '{wrong}': Convert={result.Convert}, text='{result.ConvertedText}', {result.Reason}");
            }
        }

        return (total, detected, mismatches);
    }

    private static List<string> LoadCorpusWords(string fileName)
    {
        var path = Path.Combine(RepoRoot, "tools", "Evaluate", fileName);
        if (!File.Exists(path))
        {
            return new List<string>();
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in File.ReadAllText(path).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var word = TrimNonLetters(raw);
            if (word.Length == 0)
            {
                continue;
            }

            word = word.ToLowerInvariant();
            if (seen.Add(word))
            {
                result.Add(word);
            }
        }

        return result;
    }

    private static string TrimNonLetters(string value)
    {
        var start = 0;
        var end = value.Length - 1;
        while (start <= end && !char.IsLetter(value[start]))
        {
            start++;
        }

        while (end >= start && !char.IsLetter(value[end]))
        {
            end--;
        }

        return start > end ? string.Empty : value[start..(end + 1)];
    }

    private static string RepoRoot => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", ".."));

    private static string ThisFile([CallerFilePath] string path = "") => path;

    private sealed class NoModelsProvider : ILanguageResourcesProvider
    {
        public IWordDictionary? GetDictionary(string language) => null;

        public ICharNgramModel? GetTrigrams(string language) => null;

        public ICharNgramModel? GetQuadgrams(string language) => null;
    }
}
