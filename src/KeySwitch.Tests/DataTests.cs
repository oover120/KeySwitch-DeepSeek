using System.IO;
using KeySwitch.Core.Correction;
using KeySwitch.Core.Detect;
using KeySwitch.Core.Engine;
using KeySwitch.Core.Input;
using KeySwitch.Core.Layout;
using KeySwitch.Core.Learning;
using KeySwitch.Core.PostProcess;
using KeySwitch.Data;
using Xunit;

namespace KeySwitch.Tests;

/// <summary>
/// Тесты слоя Data: пути/настройки/исключения/обучение на временных файлах и
/// интеграция с реальными встроенными ассетами (если они собраны).
/// </summary>
public class DataTests
{
    [Fact]
    public void AppPaths_TempRoot_LaysOutFilesInsideRoot()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);

        paths.EnsureCreated();

        Assert.True(Directory.Exists(paths.Root));
        Assert.True(Directory.Exists(paths.LogsDir));
        Assert.True(Directory.Exists(paths.SoundsDir));
        foreach (var file in new[] { paths.SettingsFile, paths.ExceptionsFile, paths.LearningFile, paths.LogFile })
        {
            Assert.StartsWith(paths.Root, file, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SettingsStore_RoundTrip_SavesAndLoadsValues()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        var logger = new AppLogger(paths, safeMode: true);

        var store = new SettingsStore(paths, logger);
        store.Load();

        var saved = 0;
        store.Saved += (_, _) => saved++;

        store.Current.Enabled = false;
        store.Current.Theme = "dark";
        store.Current.UndoThresholdToSuppress = 5;
        store.Save();

        Assert.Equal(1, saved);
        Assert.True(File.Exists(paths.SettingsFile));

        var reloaded = new SettingsStore(paths, logger);
        reloaded.Load();

        Assert.False(reloaded.Current.Enabled);
        Assert.Equal("dark", reloaded.Current.Theme);
        Assert.Equal(5, reloaded.Current.UndoThresholdToSuppress);
    }

    [Fact]
    public void SettingsStore_CorruptJson_QuarantinesFileAndUsesDefaults()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        var logger = new AppLogger(paths, safeMode: true);

        paths.EnsureCreated();
        File.WriteAllText(paths.SettingsFile, "{ это точно не json");

        var store = new SettingsStore(paths, logger);
        store.Load();

        Assert.True(File.Exists(paths.SettingsFile + ".bad"));
        Assert.True(store.Current.Enabled);
        Assert.Equal(3, store.Current.UndoThresholdToSuppress);
    }

    [Fact]
    public void ExceptionStore_CanAddWord_ValidatesWords()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        var logger = new AppLogger(paths, safeMode: true);
        var store = new ExceptionStore(paths, logger, resources: null);
        store.Load();

        Assert.False(store.CanAddWordException(string.Empty, out var emptyError));
        Assert.NotEmpty(emptyError);

        Assert.False(store.CanAddWordException("a", out var shortError));
        Assert.Contains("коротк", shortError, StringComparison.OrdinalIgnoreCase);

        Assert.False(store.CanAddWordException("abc123", out var digitError));
        Assert.Contains("цифр", digitError, StringComparison.OrdinalIgnoreCase);

        Assert.False(store.CanAddWordException("приветhello", out var mixedError));
        Assert.Contains("смешив", mixedError, StringComparison.OrdinalIgnoreCase);

        Assert.True(store.CanAddWordException("привет", out _));
        Assert.True(store.CanAddWordException("hello", out _));
    }

    [Fact]
    public void ExceptionStore_AddWord_BlocksInMatchingDirection()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        var logger = new AppLogger(paths, safeMode: true);
        var store = new ExceptionStore(paths, logger, resources: null);
        store.Load();

        store.AddWord("привет", ExceptionStore.DirectionBoth);
        Assert.Equal(WordVerdict.BlockedByUser, store.CheckWord("привет", LayoutCatalog.EnUsId, LayoutCatalog.RuRuId));
        Assert.Equal(WordVerdict.BlockedByUser, store.CheckWord("привет", LayoutCatalog.RuRuId, LayoutCatalog.EnUsId));

        store.AddWord("тест", ExceptionStore.DirectionRuEn);
        Assert.Equal(WordVerdict.BlockedByUser, store.CheckWord("тест", LayoutCatalog.RuRuId, LayoutCatalog.EnUsId));
        Assert.Equal(WordVerdict.Allow, store.CheckWord("тест", LayoutCatalog.EnUsId, LayoutCatalog.RuRuId));
    }

    [Fact]
    public void LearningStore_SuppressesAfterConfiguredUndoCount()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        var logger = new AppLogger(paths, safeMode: true);
        var settings = new AppSettings { UndoThresholdToSuppress = 3 };
        var store = new LearningStore(paths, logger, settings);
        store.Load();

        store.RecordUndo("ghbdtn", LayoutCatalog.EnUsId, LayoutCatalog.RuRuId);
        store.RecordUndo("ghbdtn", LayoutCatalog.EnUsId, LayoutCatalog.RuRuId);
        Assert.Equal(2, store.GetUndoCount("ghbdtn", LayoutCatalog.EnUsId, LayoutCatalog.RuRuId));
        Assert.False(store.IsSuppressed("ghbdtn", LayoutCatalog.EnUsId, LayoutCatalog.RuRuId));

        store.RecordUndo("ghbdtn", LayoutCatalog.EnUsId, LayoutCatalog.RuRuId);
        Assert.True(store.IsSuppressed("ghbdtn", LayoutCatalog.EnUsId, LayoutCatalog.RuRuId));

        var reloaded = new LearningStore(paths, logger, settings);
        reloaded.Load();
        Assert.True(reloaded.IsSuppressed("ghbdtn", LayoutCatalog.EnUsId, LayoutCatalog.RuRuId));
        Assert.Equal(3, reloaded.GetUndoCount("ghbdtn", LayoutCatalog.EnUsId, LayoutCatalog.RuRuId));
    }

    [Fact]
    public void LanguageResources_RealAssets_LoadAndDetect()
    {
        // Интеграционный тест: при отсутствии собранных ассетов не падаем, а пропускаем.
        var resources = SharedAssets.Value;
        if (resources is not { IsLoaded: true })
        {
            return;
        }

        var ru = resources.GetDictionary(LanguageResources.Russian);
        var en = resources.GetDictionary(LanguageResources.English);
        Assert.NotNull(ru);
        Assert.NotNull(en);
        Assert.True(ru!.Count > 100_000);
        Assert.True(en!.Count > 10_000);

        Assert.True(resources.GetTrigrams(LanguageResources.Russian)!.GramCount > 1000);
        Assert.True(resources.GetQuadgrams(LanguageResources.Russian)!.GramCount > 1000);

        var detector = new LayoutDetector(resources, LayoutPairRegistry.CreateDefault(), new DetectionSettings());
        var fixedWord = detector.Decide("ghbdtn", LayoutCatalog.EnUs);
        Assert.True(fixedWord.Convert);
        Assert.Equal("привет", fixedWord.ConvertedText);

        var kept = detector.Decide("привет", LayoutCatalog.RuRu);
        Assert.False(kept.Convert);

        // Одиночная буква в реальных словарях: «привет малыш b карлсон».
        var single = detector.Decide("b", LayoutCatalog.EnUs);
        Assert.True(single.Convert);
        Assert.Equal("и", single.ConvertedText);
    }

    [Theory]
    [InlineData("ghb", "при")]
    [InlineData("Ghb", "При")]
    [InlineData(",e[ufknth", "бухгалтер")]
    [InlineData(",e[ufknth,", "бухгалтер,")]
    [InlineData("jge,kbreq", "опубликуй")]
    [InlineData("jge,kbreq,", "опубликуй,")]
    [InlineData("pfievtk", "зашумел")]
    [InlineData("pfievtk,", null)]
    [InlineData("ckf;tyyjq", "слаженной")]
    [InlineData("[kt,", "хлеб")]
    [InlineData("GHB", null)]
    [InlineData("nth", null)]
    [InlineData("hello", null)]
    [InlineData("example.com", null)]
    [InlineData("readme.txt", null)]
    [InlineData("Pfievtk", "Зашумел")]
    [InlineData("PFIEVTK", null)]
    [InlineData(",e[ufknth2", null)]
    public void CompleteWord_WithRealDictionariesAndExceptions(string text, string? expected)
    {
        var resources = Assert.IsType<LanguageResources>(SharedAssets.Value);
        Assert.True(resources.IsLoaded);

        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        var logger = new AppLogger(paths, safeMode: true);
        var exceptions = new ExceptionStore(paths, logger, resources);
        var settings = new DetectionSettings();
        var detector = new LayoutDetector(resources, LayoutPairRegistry.CreateDefault(), settings);
        var engine = new CorrectionEngine(detector, settings, NullLearningStore.Instance, exceptions,
            new PostProcessorPipeline(Array.Empty<IPostProcessor>()));

        foreach (var ch in text)
        {
            Assert.True(LayoutCatalog.EnUs.TryGetKey(ch, out var virtualKey, out var shift));
            Assert.Equal(KeyRole.WordChar, KeyClassifier.Classify(virtualKey, false, false, false, ch));
            engine.AppendChar(new KeyStroke(virtualKey, shift, false), ch, LayoutCatalog.EnUsId);
        }

        var result = engine.CompleteWord(' ', terminatorAlreadyTyped: true);
        if (expected is null)
        {
            Assert.Null(result.Plan);
            return;
        }

        var plan = Assert.IsType<CorrectionPlan>(result.Plan);
        Assert.Equal(CorrectionKind.LayoutSwitch, plan.Kind);
        Assert.Equal(expected, plan.ReplacementText);
        Assert.Equal(LayoutCatalog.RuRuId, plan.TargetLayoutId);
        Assert.Equal(text.Length + 1, plan.DeleteCount);
        Assert.Equal(' ', plan.Terminator);
    }

    [Theory]
    [InlineData("pfievtk", "зашумел", LayoutCatalog.EnUsId)]
    [InlineData("зщвсфыеукы", "podcasters", LayoutCatalog.RuRuId)]
    public void UnknownWord_IsConvertedWithStrongNgramCoverage(string text, string expected, string layoutId)
    {
        var resources = Assert.IsType<LanguageResources>(SharedAssets.Value);
        Assert.True(resources.IsLoaded);
        var settings = new DetectionSettings();
        var detector = new LayoutDetector(resources, LayoutPairRegistry.CreateDefault(), settings);
        var source = LayoutCatalog.TryGet(layoutId)!;
        var target = detector.Pairs.GetAlternate(source)!;

        var result = detector.Decide(text, source);
        Assert.True(result.Convert, result.Reason);
        Assert.Equal(expected, result.ConvertedText);
        Assert.False(result.SourceInDictionary);
        Assert.False(result.TargetInDictionary);

        Assert.False(detector.Decide(text, source, target, early: true).Convert);

        settings.NoDictionaryThresholdMultiplier = 5.0;
        Assert.False(detector.Decide(text, source).Convert);
    }

    [Fact]
    public void DictionaryConflict_RespectsConfidenceAndEarlySwitch()
    {
        var resources = Assert.IsType<LanguageResources>(SharedAssets.Value);
        Assert.True(resources.IsLoaded);
        var settings = new DetectionSettings { ConfidenceThreshold = 3.0 };
        var detector = new LayoutDetector(resources, LayoutPairRegistry.CreateDefault(), settings);

        var cautious = detector.Decide("ghb", LayoutCatalog.EnUs);
        Assert.False(cautious.Convert);
        Assert.True(cautious.SourceInDictionary);
        Assert.True(cautious.TargetInDictionary);

        settings.ConfidenceThreshold = 2.0;
        var early = detector.Decide("ghb", LayoutCatalog.EnUs, LayoutCatalog.RuRu, early: true);
        Assert.False(early.Convert);

        settings.UseFrequencyWeighting = false;
        Assert.False(detector.Decide("ghb", LayoutCatalog.EnUs).Convert);
    }

    private static readonly Lazy<LanguageResources?> SharedAssets = new(() =>
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "keyswitch-tests-assets-" + Environment.ProcessId);
            var logger = new AppLogger(new AppPaths(root), safeMode: true);
            return LanguageResources.Load(logger);
        }
        catch
        {
            return null;
        }
    });

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "keyswitch-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Временная папка: не влияет на результат теста.
            }
        }
    }
}
