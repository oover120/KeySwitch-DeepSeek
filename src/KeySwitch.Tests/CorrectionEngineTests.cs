using KeySwitch.Core.Correction;
using KeySwitch.Core.Detect;
using KeySwitch.Core.Engine;
using KeySwitch.Core.Input;
using KeySwitch.Core.Learning;
using KeySwitch.Core.Layout;
using KeySwitch.Core.PostProcess;
using KeySwitch.Tests.TestSupport;
using Xunit;

namespace KeySwitch.Tests;

public class CorrectionEngineTests
{
    private static CorrectionEngine CreateEngine(
        DetectionSettings? settings = null,
        FakeLearningStore? learning = null,
        FakeExceptionPolicy? exceptions = null,
        PostProcessorPipeline? postProcessors = null)
    {
        settings ??= TestEnvironment.DefaultSettings();
        return new CorrectionEngine(
            TestEnvironment.CreateDetector(settings),
            settings,
            learning ?? new FakeLearningStore(),
            exceptions ?? new FakeExceptionPolicy(),
            postProcessors ?? new PostProcessorPipeline(Array.Empty<IPostProcessor>()));
    }

    private static void Type(CorrectionEngine engine, string text, string layoutId)
    {
        foreach (var ch in text)
        {
            engine.AppendChar(new KeyStroke(0x41, false, false), ch, layoutId);
        }
    }

    [Fact]
    public void CompleteWord_WrongLayout_ProducesSwitchPlan()
    {
        var engine = CreateEngine();
        Type(engine, "ghbdtn", LayoutCatalog.EnUsId);

        var result = engine.CompleteWord(null, terminatorAlreadyTyped: false);

        Assert.True(result.HasPlan);
        Assert.NotNull(result.Word);
        Assert.Equal("ghbdtn", result.Word!.Text);
        var plan = result.Plan!;
        Assert.Equal(CorrectionKind.LayoutSwitch, plan.Kind);
        Assert.Equal("ghbdtn", plan.OriginalText);
        Assert.Equal("привет", plan.ReplacementText);
        Assert.Equal(6, plan.DeleteCount);
        Assert.Null(plan.Terminator);
        Assert.Equal("привет", plan.TextToType);
        Assert.Equal(LayoutCatalog.EnUsId, plan.SourceLayoutId);
        Assert.Equal(LayoutCatalog.RuRuId, plan.TargetLayoutId);
        Assert.InRange(plan.Confidence, 0.0, 1.0);
        Assert.Contains("Δ=", plan.Reason);
        Assert.Equal(plan.Reason, result.Note);

        Assert.Same(plan, engine.LastApplied);
        Assert.True(engine.CanUndo);
        Assert.True(engine.Buffer.IsEmpty);
        Assert.Equal("ghbdtn", engine.LastWord!.Text);
    }

    [Fact]
    public void CompleteWord_TerminatorNotTyped_TextToTypeContainsTerminator()
    {
        // Контракт (docs/CONTRACTS.md:209 и :226): при CompleteWord(ch, false) Hooks глушит клавишу
        // (Handled = true), поэтому терминатор обязан быть внутри TextToType.
        var engine = CreateEngine();
        Type(engine, "ghbdtn", LayoutCatalog.EnUsId);

        var plan = engine.CompleteWord(' ', terminatorAlreadyTyped: false).Plan!;

        Assert.Equal(' ', plan.Terminator);
        Assert.Equal("привет ", plan.TextToType);
        Assert.Equal(6, plan.DeleteCount);
    }

    [Fact]
    public void CompleteWord_TerminatorAlreadyTyped_DeletesItAndRetypesIt()
    {
        // Контракт (docs/CONTRACTS.md:209 и :226): если терминатор уже напечатан, его нужно
        // удалить (DeleteCount + 1) и вернуть в TextToType — иначе пробел потеряется.
        var engine = CreateEngine();
        Type(engine, "ghbdtn", LayoutCatalog.EnUsId);

        var plan = engine.CompleteWord(' ', terminatorAlreadyTyped: true).Plan!;

        Assert.Equal(' ', plan.Terminator);
        Assert.Equal("привет ", plan.TextToType);
        Assert.Equal(7, plan.DeleteCount);
    }

    [Fact]
    public void CompleteWord_CorrectWord_NoPlan()
    {
        var learning = new FakeLearningStore();
        var engine = CreateEngine(learning: learning);
        Type(engine, "привет", LayoutCatalog.RuRuId);

        var result = engine.CompleteWord(null, terminatorAlreadyTyped: false);

        Assert.False(result.HasPlan);
        Assert.Null(result.Plan);
        Assert.NotNull(result.Word);
        Assert.Equal("привет", result.Word!.Text);
        Assert.Null(result.Note);
        Assert.False(engine.CanUndo);
        Assert.Empty(learning.Accepted);
    }

    [Fact]
    public void CompleteWord_EmptyBuffer_ReturnsEmpty()
    {
        var engine = CreateEngine();

        var result = engine.CompleteWord(' ', terminatorAlreadyTyped: false);

        Assert.Same(EngineResult.Empty, result);
        Assert.Null(result.Word);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void CompleteWord_TooShortWord_NoPlanButWordIsKept()
    {
        var engine = CreateEngine();
        Type(engine, "g", LayoutCatalog.EnUsId);

        var result = engine.CompleteWord(null, terminatorAlreadyTyped: false);

        Assert.False(result.HasPlan);
        Assert.Equal("g", result.Word!.Text);
    }

    [Fact]
    public void Exceptions_BlockAutomaticSwitch_ButNotManual()
    {
        var exceptions = new FakeExceptionPolicy { Verdict = WordVerdict.BlockedByUser };
        var engine = CreateEngine(exceptions: exceptions);
        Type(engine, "ghbdtn", LayoutCatalog.EnUsId);

        var automatic = engine.CompleteWord(null, terminatorAlreadyTyped: false);

        Assert.False(automatic.HasPlan);
        Assert.Single(exceptions.CheckedWords);
        Assert.Equal("ghbdtn", exceptions.CheckedWords[0]);

        var manual = engine.ConvertManually();

        Assert.True(manual.HasPlan);
        Assert.Equal(CorrectionKind.LayoutSwitch, manual.Plan!.Kind);
        Assert.Equal("привет", manual.Plan.ReplacementText);
        // Ручная конвертация не должна повторно спрашивать политику исключений.
        Assert.Single(exceptions.CheckedWords);
    }

    [Fact]
    public void LearningSuppressedWord_BlocksAutomaticSwitch()
    {
        var learning = new FakeLearningStore();
        learning.Suppressed.Add("ghbdtn");
        var engine = CreateEngine(learning: learning);
        Type(engine, "ghbdtn", LayoutCatalog.EnUsId);

        var result = engine.CompleteWord(null, terminatorAlreadyTyped: false);

        Assert.False(result.HasPlan);
    }

    [Fact]
    public void LearningSuppression_AfterThreeUndos_BlocksSwitch()
    {
        var learning = new FakeLearningStore();
        learning.UndoCounts[("ghbdtn", LayoutCatalog.EnUsId, LayoutCatalog.RuRuId)] = 3;
        var engine = CreateEngine(learning: learning);
        Type(engine, "ghbdtn", LayoutCatalog.EnUsId);

        Assert.False(engine.CompleteWord(null, terminatorAlreadyTyped: false).HasPlan);
    }

    [Fact]
    public void AutomaticSwitch_RecordsAcceptedWord()
    {
        var learning = new FakeLearningStore();
        var engine = CreateEngine(learning: learning);
        Type(engine, "ghbdtn", LayoutCatalog.EnUsId);

        engine.CompleteWord(null, terminatorAlreadyTyped: false);

        var accepted = Assert.Single(learning.Accepted);
        Assert.Equal("ghbdtn", accepted.Word);
        Assert.Equal(LayoutCatalog.EnUsId, accepted.Source);
        Assert.Equal(LayoutCatalog.RuRuId, accepted.Target);
    }

    [Fact]
    public void ConvertManually_WithBuffer_ProducesPlan()
    {
        var engine = CreateEngine();
        Type(engine, "ghbdtn", LayoutCatalog.EnUsId);

        var result = engine.ConvertManually();

        Assert.True(result.HasPlan);
        Assert.Equal("ghbdtn", result.Plan!.OriginalText);
        Assert.Equal("привет", result.Plan.ReplacementText);
        Assert.True(engine.Buffer.IsEmpty);
        Assert.Equal("ghbdtn", engine.LastWord!.Text);
    }

    [Fact]
    public void ConvertManually_WithoutBuffer_UsesLastCompletedWord()
    {
        var engine = CreateEngine();
        Type(engine, "ghbdtn", LayoutCatalog.EnUsId);
        engine.CompleteWord(null, terminatorAlreadyTyped: false);

        var result = engine.ConvertManually();

        Assert.True(result.HasPlan);
        Assert.Equal("ghbdtn", result.Plan!.OriginalText);
    }

    [Fact]
    public void ConvertManually_NoConversionNeeded_ReturnsNote()
    {
        var engine = CreateEngine();
        Type(engine, "привет", LayoutCatalog.RuRuId);
        engine.CompleteWord(null, terminatorAlreadyTyped: false);

        var result = engine.ConvertManually();

        Assert.False(result.HasPlan);
        Assert.Null(result.Plan);
        Assert.Equal("конвертация не требуется", result.Note);
        Assert.Equal("привет", result.Word!.Text);
    }

    [Fact]
    public void ConvertManually_EmptyEngine_ReturnsEmpty()
    {
        var engine = CreateEngine();
        Assert.Same(EngineResult.Empty, engine.ConvertManually());
    }

    [Fact]
    public void ConvertSelection_WrongLayout_ProducesSelectionPlan()
    {
        var learning = new FakeLearningStore();
        var engine = CreateEngine(learning: learning);

        var plan = engine.ConvertSelection("ghbdtn", LayoutCatalog.EnUsId);

        Assert.NotNull(plan);
        Assert.Equal(CorrectionKind.Selection, plan!.Kind);
        Assert.Equal("ghbdtn", plan.OriginalText);
        Assert.Equal("привет", plan.ReplacementText);
        Assert.Equal(0, plan.DeleteCount);
        Assert.Null(plan.Terminator);
        Assert.Equal("привет", plan.TextToType);
        Assert.Same(plan, engine.LastApplied);

        var manual = Assert.Single(learning.ManualCorrections);
        Assert.Equal("ghbdtn", manual.Original);
        Assert.Equal("привет", manual.Corrected);
        Assert.Equal(LayoutCatalog.EnUsId, manual.Source);
        Assert.Equal(LayoutCatalog.RuRuId, manual.Target);
    }

    [Fact]
    public void ConvertSelection_CorrectWord_ReturnsNull()
    {
        var learning = new FakeLearningStore();
        var engine = CreateEngine(learning: learning);

        Assert.Null(engine.ConvertSelection("привет", LayoutCatalog.RuRuId));
        Assert.Empty(learning.ManualCorrections);
        Assert.False(engine.CanUndo);
    }

    [Fact]
    public void ConvertSelection_InvalidArguments_ReturnsNull()
    {
        var engine = CreateEngine();

        Assert.Null(engine.ConvertSelection(string.Empty, LayoutCatalog.EnUsId));
        Assert.Null(engine.ConvertSelection("   ", LayoutCatalog.EnUsId));
        Assert.Null(engine.ConvertSelection("ghbdtn", "xx-XX"));
    }

    [Fact]
    public void TryUndo_AfterSwitch_RestoresOriginal()
    {
        var learning = new FakeLearningStore();
        var engine = CreateEngine(learning: learning);
        Type(engine, "ghbdtn", LayoutCatalog.EnUsId);
        var plan = engine.CompleteWord(null, terminatorAlreadyTyped: false).Plan!;

        var undone = engine.TryUndo(out var undo);

        Assert.True(undone);
        Assert.NotNull(undo);
        Assert.Equal(CorrectionKind.LayoutSwitch, undo!.Kind);
        Assert.Equal("привет", undo.CurrentText);
        Assert.Equal("ghbdtn", undo.RestoreText);
        Assert.Equal(6, undo.DeleteCount);
        Assert.Equal("ghbdtn", undo.TextToType);
        Assert.Equal(LayoutCatalog.EnUsId, undo.RestoreLayoutId);
        Assert.Equal(LayoutCatalog.RuRuId, undo.CurrentLayoutId);
        Assert.Null(undo.Terminator);
        Assert.False(engine.CanUndo);

        var recorded = Assert.Single(learning.Undos);
        Assert.Equal("ghbdtn", recorded.Word);
        Assert.Equal(plan.SourceLayoutId, recorded.Source);
        Assert.Equal(plan.TargetLayoutId, recorded.Target);
    }

    [Fact]
    public void TryUndo_AfterSwitchWithTerminator_DeletesTerminatorToo()
    {
        // Контракт: если план печатал слово с терминатором, отмена удаляет и его.
        var engine = CreateEngine();
        Type(engine, "ghbdtn", LayoutCatalog.EnUsId);
        engine.CompleteWord(' ', terminatorAlreadyTyped: false);

        Assert.True(engine.TryUndo(out var undo));
        Assert.Equal(7, undo!.DeleteCount);
        Assert.Equal("ghbdtn ", undo.TextToType);
        Assert.Equal(' ', undo.Terminator);
    }

    [Fact]
    public void TryUndo_WithoutPlan_ReturnsFalse()
    {
        var engine = CreateEngine();

        Assert.False(engine.TryUndo(out var undo));
        Assert.Null(undo);
    }

    [Fact]
    public void TryUndo_Twice_SecondReturnsFalse()
    {
        var engine = CreateEngine();
        Type(engine, "ghbdtn", LayoutCatalog.EnUsId);
        engine.CompleteWord(null, terminatorAlreadyTyped: false);

        Assert.True(engine.TryUndo(out _));
        Assert.False(engine.TryUndo(out var undo));
        Assert.Null(undo);
    }

    [Fact]
    public void ClearUndo_DisablesUndo()
    {
        var engine = CreateEngine();
        Type(engine, "ghbdtn", LayoutCatalog.EnUsId);
        engine.CompleteWord(null, terminatorAlreadyTyped: false);

        engine.ClearUndo();

        Assert.False(engine.CanUndo);
        Assert.False(engine.TryUndo(out _));
    }

    [Fact]
    public void TryEarlySwitch_Disabled_ReturnsNull()
    {
        var engine = CreateEngine();
        Type(engine, "ghbdtn", LayoutCatalog.EnUsId);

        Assert.Null(engine.TryEarlySwitch());
        Assert.Equal(6, engine.Buffer.Length);
    }

    [Fact]
    public void TryEarlySwitch_Enabled_ProducesPlanAndClearsBuffer()
    {
        var settings = TestEnvironment.DefaultSettings();
        settings.EarlySwitchEnabled = true;
        settings.EarlySwitchThreshold = 3.0;
        var engine = CreateEngine(settings);
        Type(engine, "ghbdtn", LayoutCatalog.EnUsId);

        var plan = engine.TryEarlySwitch();

        Assert.NotNull(plan);
        Assert.Equal(CorrectionKind.LayoutSwitch, plan!.Kind);
        Assert.Equal("ghbdtn", plan.OriginalText);
        Assert.Equal("привет", plan.ReplacementText);
        Assert.Equal(6, plan.DeleteCount);
        Assert.Null(plan.Terminator);
        Assert.StartsWith("раннее переключение:", plan.Reason);
        Assert.True(engine.Buffer.IsEmpty);
        Assert.Same(plan, engine.LastApplied);
        Assert.Equal("ghbdtn", engine.LastWord!.Text);
    }

    [Fact]
    public void TryEarlySwitch_TooShort_ReturnsNull()
    {
        var settings = TestEnvironment.DefaultSettings();
        settings.EarlySwitchEnabled = true;
        settings.EarlySwitchMinLength = 4;
        var engine = CreateEngine(settings);
        Type(engine, "ghb", LayoutCatalog.EnUsId);

        Assert.Null(engine.TryEarlySwitch());
        Assert.Equal(3, engine.Buffer.Length);
    }

    [Fact]
    public void TryEarlySwitch_NonDictionaryCandidate_ReturnsNull()
    {
        var settings = TestEnvironment.DefaultSettings();
        settings.EarlySwitchEnabled = true;
        settings.EarlySwitchMinLength = 3;
        settings.EarlySwitchThreshold = 1.0;
        var engine = CreateEngine(settings);
        Type(engine, "ghb", LayoutCatalog.EnUsId);

        Assert.Null(engine.TryEarlySwitch());
        Assert.Equal(3, engine.Buffer.Length);
    }

    [Fact]
    public void TryEarlySwitch_AfterPlan_TryUndoRestoresWord()
    {
        var settings = TestEnvironment.DefaultSettings();
        settings.EarlySwitchEnabled = true;
        settings.EarlySwitchThreshold = 3.0;
        var engine = CreateEngine(settings);
        Type(engine, "ghbdtn", LayoutCatalog.EnUsId);
        engine.TryEarlySwitch();

        Assert.True(engine.TryUndo(out var undo));
        Assert.Equal("ghbdtn", undo!.RestoreText);
        Assert.Equal(6, undo.DeleteCount);
    }

    [Fact]
    public void PostProcess_Typo_ProducesTypoPlan()
    {
        var typos = new FakeTypoDictionary();
        typos.Map["тест"] = "тэст";
        var engine = CreateEngine(postProcessors: new PostProcessorPipeline(new IPostProcessor[] { new TypoCorrector(typos) }));
        Type(engine, "тест", LayoutCatalog.RuRuId);

        var result = engine.CompleteWord(null, terminatorAlreadyTyped: false);

        Assert.True(result.HasPlan);
        var plan = result.Plan!;
        Assert.Equal(CorrectionKind.Typo, plan.Kind);
        Assert.Equal("тест", plan.OriginalText);
        Assert.Equal("тэст", plan.ReplacementText);
        Assert.Equal(4, plan.DeleteCount);
        Assert.Equal(LayoutCatalog.RuRuId, plan.SourceLayoutId);
        Assert.Equal(LayoutCatalog.RuRuId, plan.TargetLayoutId);
        Assert.Equal(1.0, plan.Confidence);
        Assert.Equal("постобработка typo", plan.Reason);
    }

    [Fact]
    public void PostProcess_Caps_ProducesCapsPlan()
    {
        var engine = CreateEngine(postProcessors: new PostProcessorPipeline(new IPostProcessor[] { new CapsFixer() }));
        Type(engine, "ПРивет", LayoutCatalog.RuRuId);

        var result = engine.CompleteWord(null, terminatorAlreadyTyped: false);

        Assert.True(result.HasPlan);
        Assert.Equal(CorrectionKind.CapsFix, result.Plan!.Kind);
        Assert.Equal("Привет", result.Plan.ReplacementText);
        Assert.Equal("постобработка caps", result.Plan.Reason);
    }

    [Fact]
    public void PostProcess_Yo_ProducesYoPlan()
    {
        var yo = new FakeYoDictionary();
        yo.Map["тест"] = "тёст";
        var engine = CreateEngine(postProcessors: new PostProcessorPipeline(new IPostProcessor[] { new YoFixer(yo) }));
        Type(engine, "тест", LayoutCatalog.RuRuId);

        var result = engine.CompleteWord(null, terminatorAlreadyTyped: false);

        Assert.True(result.HasPlan);
        Assert.Equal(CorrectionKind.YoFix, result.Plan!.Kind);
        Assert.Equal("тёст", result.Plan.ReplacementText);
        Assert.Equal("постобработка yo", result.Plan.Reason);
    }

    [Fact]
    public void PostProcess_Disabled_NoPlanForCorrectWord()
    {
        var engine = CreateEngine();
        Type(engine, "тест", LayoutCatalog.RuRuId);

        Assert.False(engine.CompleteWord(null, terminatorAlreadyTyped: false).HasPlan);
    }

    [Fact]
    public void PostProcess_WithTerminator_IncludesTerminatorInTextToType()
    {
        var typos = new FakeTypoDictionary();
        typos.Map["тест"] = "тэст";
        var engine = CreateEngine(postProcessors: new PostProcessorPipeline(new IPostProcessor[] { new TypoCorrector(typos) }));
        Type(engine, "тест", LayoutCatalog.RuRuId);

        var plan = engine.CompleteWord(' ', terminatorAlreadyTyped: false).Plan!;

        Assert.Equal(' ', plan.Terminator);
        Assert.Equal("тэст ", plan.TextToType);
    }

    [Fact]
    public void Reset_ClearsBuffer()
    {
        var engine = CreateEngine();
        Type(engine, "ghbdtn", LayoutCatalog.EnUsId);

        engine.Reset();

        Assert.True(engine.Buffer.IsEmpty);
        Assert.Same(EngineResult.Empty, engine.CompleteWord(null, terminatorAlreadyTyped: false));
    }

    [Fact]
    public void Backspace_RemovesLastChar()
    {
        var engine = CreateEngine();
        Type(engine, "ghbdtn", LayoutCatalog.EnUsId);

        engine.Backspace();

        Assert.Equal(5, engine.Buffer.Length);
        Assert.Equal("ghbdt", engine.Buffer.Text);
    }

    [Fact]
    public void Buffer_KeepsLayoutOfFirstCharacter()
    {
        var engine = CreateEngine();
        Type(engine, "gh", LayoutCatalog.EnUsId);
        Type(engine, "bd", LayoutCatalog.RuRuId);

        Assert.Equal(LayoutCatalog.EnUsId, engine.Buffer.LayoutId);
        Assert.Equal("ghbd", engine.Buffer.Text);
    }
}
