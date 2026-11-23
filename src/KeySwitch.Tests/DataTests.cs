using System.IO;
using KeySwitch.Core.Correction;
using KeySwitch.Core.Detect;
using KeySwitch.Core.Engine;
using KeySwitch.Core.Input;
using KeySwitch.Core.Layout;
using KeySwitch.Core.Learning;
using KeySwitch.Core.PostProcess;
using KeySwitch.Data;
using KeySwitch.Tests.TestSupport;
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
    public void LearningStore_ReusesOnlyConfirmedLayoutCorrections_AfterRestart()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        var logger = new AppLogger(paths, safeMode: true);
        var settings = new AppSettings();
        var store = new LearningStore(paths, logger, settings);
        const string en = LayoutCatalog.EnUsId;
        const string ru = LayoutCatalog.RuRuId;

        store.RecordAccepted("ghbdtn", en, ru);
        Assert.False(store.TryGetManualCorrection("ghbdtn", en, ru, out _));
        Assert.False(store.RecordManualCorrection("ghbdtn vbh", "привет мир", en, ru));
        Assert.True(store.RecordManualCorrection("ghbdtn", "привет", en, ru));

        store = new LearningStore(paths, logger, settings);
        store.Load();
        Assert.True(store.TryGetManualCorrection("Ghbdtn", en, ru, out var titleCase));
        Assert.Equal("Привет", titleCase);
        Assert.False(store.TryGetManualCorrection("ghbdtn", ru, en, out _));
        settings.LearningEnabled = false;
        Assert.False(store.TryGetManualCorrection("ghbdtn", en, ru, out _));
        settings.LearningEnabled = true;
        store.RecordUndo("ghbdtn", en, ru);
        Assert.False(store.TryGetManualCorrection("ghbdtn", en, ru, out _));
    }

    [Fact]
    public void Engine_ReciprocalLearnedCorrections_PreferTheDictionaryWord()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        var logger = new AppLogger(paths, safeMode: true);
        var appSettings = new AppSettings();
        const string en = LayoutCatalog.EnUsId;
        const string ru = LayoutCatalog.RuRuId;
        var store = new LearningStore(paths, logger, appSettings);
        Assert.True(store.RecordManualCorrection("пфьу", "game", ru, en));
        Assert.True(store.RecordManualCorrection("game", "пфьу", en, ru));

        store = new LearningStore(paths, logger, appSettings);
        store.Load();
        var settings = TestEnvironment.DefaultSettings();
        var resources = new TestLanguageResources(["привет"], ["game"]);
        var detector = new LayoutDetector(resources, LayoutPairRegistry.CreateDefault(), settings);
        var engine = new CorrectionEngine(detector, settings, store, NullExceptionPolicy.Instance,
            new PostProcessorPipeline(Array.Empty<IPostProcessor>()));

        Assert.Equal("game ", CompleteInput(engine, "пфьу", ru).Plan?.TextToType);
        Assert.False(CompleteInput(engine, "game", en).HasPlan);
        Assert.False(CompleteInput(engine, "GAME", en).HasPlan);
        Assert.Equal("ПФЬУ ", engine.ForceConvert().Plan?.TextToType);
        Assert.Equal("game ", CompleteInput(engine, "пфьу", ru).Plan?.TextToType);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CompleteWord_LearnsWhenEntireWordIsErasedAndRetypedInOtherLayout(bool afterSpaces, bool wholeWord)
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        var logger = new AppLogger(paths, safeMode: true);
        var store = new LearningStore(paths, logger, new AppSettings());
        var settings = new DetectionSettings { ConfidenceThreshold = 100 };
        var engine = new CorrectionEngine(TestEnvironment.CreateDetector(settings), settings, store,
            new ExceptionStore(paths, logger, null), new PostProcessorPipeline(Array.Empty<IPostProcessor>()));
        const string typed = "ghbdtn";

        AppendInput(engine, typed, LayoutCatalog.EnUsId);
        if (afterSpaces)
        {
            Assert.False(engine.CompleteWord(' ', terminatorAlreadyTyped: true).HasPlan);
            engine.CompleteWord(' ', terminatorAlreadyTyped: true);
        }

        engine.OnLayoutShortcut();
        if (wholeWord)
            engine.Backspace(wholeWord: true);
        else
        {
            for (var i = 0; i < typed.Length + (afterSpaces ? 2 : 0); i++)
                engine.Backspace();
        }

        engine.OnLayoutShortcut();
        var retyped = CompleteInput(engine, "привет", LayoutCatalog.RuRuId);
        Assert.False(retyped.HasPlan);
        Assert.Contains("запомнено", retyped.Note);

        var learned = CompleteInput(engine, typed, LayoutCatalog.EnUsId);
        Assert.Equal("привет ", learned.Plan?.TextToType);
    }

    [Fact]
    public void CompleteWord_DoesNotLearnFromUnrelatedRetypeOrAnotherWindow()
    {
        var learning = new FakeLearningStore();
        var settings = new DetectionSettings { ConfidenceThreshold = 100 };
        var engine = new CorrectionEngine(TestEnvironment.CreateDetector(settings), settings, learning,
            NullExceptionPolicy.Instance, new PostProcessorPipeline(Array.Empty<IPostProcessor>()));

        AppendInput(engine, "ghbdtn", LayoutCatalog.EnUsId);
        engine.Backspace(wholeWord: true);
        CompleteInput(engine, "добрый", LayoutCatalog.RuRuId);
        Assert.Empty(learning.ManualCorrections);

        AppendInput(engine, "ghbdtn", LayoutCatalog.EnUsId);
        engine.Backspace(wholeWord: true);
        engine.Reset(forgetLastWord: true);
        CompleteInput(engine, "привет", LayoutCatalog.RuRuId);
        Assert.Empty(learning.ManualCorrections);
    }

    [Fact]
    public void ForceConvert_ConfirmsFutureSwitch_AndDoubleShiftUndoKeepsOriginal()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        var logger = new AppLogger(paths, safeMode: true);
        var exceptions = new ExceptionStore(paths, logger, null);
        var store = new LearningStore(paths, logger, new AppSettings(), exceptions);
        var settings = new DetectionSettings { ConfidenceThreshold = 100 };
        var engine = new CorrectionEngine(TestEnvironment.CreateDetector(settings), settings, store,
            exceptions, new PostProcessorPipeline(Array.Empty<IPostProcessor>()));
        const string en = LayoutCatalog.EnUsId;
        const string ru = LayoutCatalog.RuRuId;

        Assert.False(CompleteInput(engine, "ghbdtn", en).HasPlan);
        Assert.Equal("привет ", engine.ForceConvert().Plan?.TextToType);
        Assert.Equal("привет ", CompleteInput(engine, "ghbdtn", en).Plan?.TextToType);
        Assert.True(engine.TryUndo(out var undo, confirmOriginal: true));
        Assert.Equal("ghbdtn ", undo!.TextToType);
        Assert.Equal(WordVerdict.BlockedByLearning, exceptions.CheckWord("ghbdtn", en, ru));
        Assert.False(CompleteInput(engine, "ghbdtn", en).HasPlan);

        Assert.Equal("привет ", engine.ForceConvert().Plan?.TextToType);
        Assert.Equal("привет ", CompleteInput(engine, "ghbdtn", en).Plan?.TextToType);
        exceptions.AddWord("ghbdtn", ExceptionStore.DirectionEnRu);
        Assert.False(CompleteInput(engine, "ghbdtn", en).HasPlan);
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

        var result = CompleteInput(engine, text, LayoutCatalog.EnUsId);
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
    [InlineData(true, "хорошая!")]
    [InlineData(false, "хрошая!")]
    public void CompleteWord_CorrectsMissingLetterAfterLayoutSwitch_WhenEnabled(bool correctTypos, string expected)
    {
        var resources = Assert.IsType<LanguageResources>(SharedAssets.Value);
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        var logger = new AppLogger(paths, safeMode: true);
        var settings = new DetectionSettings();
        var engine = new CorrectionEngine(new LayoutDetector(resources, LayoutPairRegistry.CreateDefault(), settings),
            settings, NullLearningStore.Instance, new ExceptionStore(paths, logger, resources),
            new PostProcessorPipeline(Array.Empty<IPostProcessor>(), correctTypos ? new MissingLetterCorrector(resources) : null));

        const string typed = "[hjifz";
        var result = CompleteInput(engine, typed, LayoutCatalog.EnUsId, '!');
        var plan = Assert.IsType<CorrectionPlan>(result.Plan);
        Assert.Equal(CorrectionKind.LayoutSwitch, plan.Kind);
        Assert.Equal(LayoutCatalog.RuRuId, plan.TargetLayoutId);
        Assert.Equal(expected, plan.TextToType);
        Assert.Equal(typed.Length + 1, plan.DeleteCount);
        Assert.True(engine.TryUndo(out var undo));
        Assert.Equal(typed + "!", undo!.TextToType);
        Assert.Equal(expected.Length, undo.DeleteCount);
    }

    [Fact]
    public void CompleteWord_UsesTwoKnownWordsAsContext_AndDropsItAcrossBoundaries()
    {
        var resources = Assert.IsType<LanguageResources>(SharedAssets.Value);
        var settings = new DetectionSettings();
        var engine = new CorrectionEngine(new LayoutDetector(resources, LayoutPairRegistry.CreateDefault(), settings),
            settings, NullLearningStore.Instance, NullExceptionPolicy.Instance,
            new PostProcessorPipeline(Array.Empty<IPostProcessor>()));

        const string typed = "ффкргы";
        Assert.Null(CompleteInput(engine, typed, LayoutCatalog.RuRuId).Plan);
        CompleteInput(engine, "we", LayoutCatalog.EnUsId);
        Assert.Null(CompleteInput(engine, typed, LayoutCatalog.RuRuId).Plan);
        CompleteInput(engine, "we", LayoutCatalog.EnUsId);
        CompleteInput(engine, "visited", LayoutCatalog.EnUsId);
        var withContext = Assert.IsType<CorrectionPlan>(CompleteInput(engine, typed, LayoutCatalog.RuRuId).Plan);
        Assert.Equal("aarhus", withContext.ReplacementText);
        Assert.Contains("контекст", withContext.Reason);

        CompleteInput(engine, "we", LayoutCatalog.EnUsId);
        CompleteInput(engine, "visited", LayoutCatalog.EnUsId, '\t');
        Assert.Null(CompleteInput(engine, typed, LayoutCatalog.RuRuId).Plan);

        CompleteInput(engine, "we", LayoutCatalog.EnUsId);
        CompleteInput(engine, "visited", LayoutCatalog.EnUsId);
        engine.Reset(forgetLastWord: true);
        Assert.Null(CompleteInput(engine, typed, LayoutCatalog.RuRuId).Plan);

        CompleteInput(engine, "we", LayoutCatalog.EnUsId);
        CompleteInput(engine, "visited", LayoutCatalog.EnUsId);
        engine.Backspace();
        Assert.Null(CompleteInput(engine, typed, LayoutCatalog.RuRuId).Plan);
    }

    [Fact]
    public void CompleteWord_KeepsValidEnglishWordsInRussianContext()
    {
        var resources = Assert.IsType<LanguageResources>(SharedAssets.Value);
        var settings = new DetectionSettings();
        var engine = new CorrectionEngine(new LayoutDetector(resources, LayoutPairRegistry.CreateDefault(), settings),
            settings, NullLearningStore.Instance, NullExceptionPolicy.Instance,
            new PostProcessorPipeline(Array.Empty<IPostProcessor>()));

        CompleteInput(engine, "сегодня", LayoutCatalog.RuRuId);
        CompleteInput(engine, "открыли", LayoutCatalog.RuRuId);
        Assert.Null(CompleteInput(engine, "GitHub", LayoutCatalog.EnUsId).Plan);
    }

    private static EngineResult CompleteInput(CorrectionEngine engine, string text, string layoutId, char terminator = ' ')
    {
        AppendInput(engine, text, layoutId);
        return engine.CompleteWord(terminator, terminatorAlreadyTyped: true);
    }

    private static void AppendInput(CorrectionEngine engine, string text, string layoutId)
    {
        var layout = LayoutCatalog.TryGet(layoutId)!;
        foreach (var ch in text)
        {
            Assert.True(layout.TryGetKey(ch, out var virtualKey, out var shift));
            Assert.Equal(KeyRole.WordChar, KeyClassifier.Classify(virtualKey, false, false, false, ch));
            engine.AppendChar(new KeyStroke(virtualKey, shift, false), ch, layoutId);
        }
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
