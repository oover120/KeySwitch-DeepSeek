using KeySwitch.Core.Correction;
using KeySwitch.Core.Detect;
using KeySwitch.Core.Engine;
using KeySwitch.Core.Input;
using KeySwitch.Core.Layout;
using KeySwitch.Core.PostProcess;
using KeySwitch.Tests.TestSupport;
using Xunit;

namespace KeySwitch.Tests;

/// <summary>
/// Принудительная конвертация по двойному тапу Shift: детектор слово оставил,
/// а пользователь знает, что раскладка не та.
/// </summary>
public class ForceConvertTests
{
    private static CorrectionEngine CreateEngine(DetectionSettings? settings = null)
    {
        settings ??= TestEnvironment.DefaultSettings();
        return new CorrectionEngine(
            TestEnvironment.CreateDetector(settings),
            settings,
            new FakeLearningStore(),
            new FakeExceptionPolicy(),
            new PostProcessorPipeline(Array.Empty<IPostProcessor>()));
    }

    private static void Type(CorrectionEngine engine, string text, string layoutId)
    {
        foreach (var ch in text)
        {
            engine.AppendChar(new KeyStroke(0x41, false, false), ch, layoutId);
        }
    }

    [Fact]
    public void CompleteWord_CorrectLayout_ClearsUndo()
    {
        var engine = CreateEngine();

        Type(engine, "ghbdtn", LayoutCatalog.EnUsId);
        Assert.True(engine.CompleteWord(null, terminatorAlreadyTyped: false).HasPlan);
        Assert.True(engine.CanUndo);

        // Следующее слово детектор оставил — отменять больше нечего.
        Type(engine, "hello", LayoutCatalog.EnUsId);
        var kept = engine.CompleteWord(' ', terminatorAlreadyTyped: true);

        Assert.Null(kept.Plan);
        Assert.False(engine.CanUndo);
    }

    [Fact]
    public void ForceConvert_WordKeptByDetector_ConvertsAnyway()
    {
        var engine = CreateEngine();

        Type(engine, "hello", LayoutCatalog.EnUsId);
        var kept = engine.CompleteWord(' ', terminatorAlreadyTyped: true);
        Assert.Null(kept.Plan);

        var forced = engine.ForceConvert();

        Assert.True(forced.HasPlan);
        var plan = forced.Plan!;
        Assert.Equal(CorrectionKind.LayoutSwitch, plan.Kind);
        Assert.Equal("hello", plan.OriginalText);
        Assert.Equal("руддщ", plan.ReplacementText);
        Assert.Equal(6, plan.DeleteCount);
        Assert.Equal(' ', plan.Terminator);
        Assert.Equal("руддщ ", plan.TextToType);
        Assert.Equal(LayoutCatalog.EnUsId, plan.SourceLayoutId);
        Assert.Equal(LayoutCatalog.RuRuId, plan.TargetLayoutId);
        Assert.Equal(1.0, plan.Confidence);
        Assert.Contains("принудительно", plan.Reason);
        Assert.True(engine.CanUndo);
    }

    [Fact]
    public void ForceConvert_CurrentBuffer_ConvertsWithoutTerminator()
    {
        var engine = CreateEngine();

        Type(engine, "hello", LayoutCatalog.EnUsId);
        var forced = engine.ForceConvert();

        Assert.True(forced.HasPlan);
        var plan = forced.Plan!;
        Assert.Equal("руддщ", plan.ReplacementText);
        Assert.Equal(5, plan.DeleteCount);
        Assert.Null(plan.Terminator);
        Assert.Equal("руддщ", plan.TextToType);
        Assert.Equal("hello", engine.LastWord!.Text);
    }

    [Fact]
    public void ForceConvert_ShortWord_IgnoresMinimumLength()
    {
        var settings = TestEnvironment.DefaultSettings();
        settings.MinWordLength = 4;
        var engine = CreateEngine(settings);

        // Пользователь нажал двойной Shift осознанно — минимальная длина не помеха.
        Type(engine, "abc", LayoutCatalog.EnUsId);
        var forced = engine.ForceConvert();

        Assert.True(forced.HasPlan);
        Assert.Equal("фис", forced.Plan!.ReplacementText);
        Assert.Equal(3, forced.Plan.DeleteCount);
        Assert.True(engine.CanUndo);
    }

    [Fact]
    public void ForceConvert_SingleLetter_ConvertsAnyway()
    {
        var engine = CreateEngine();

        Type(engine, "b", LayoutCatalog.EnUsId);
        var forced = engine.ForceConvert();

        Assert.True(forced.HasPlan);
        Assert.Equal("и", forced.Plan!.ReplacementText);
        Assert.Equal(1, forced.Plan.DeleteCount);
    }

    [Fact]
    public void ForceConvert_NoWord_ReturnsEmpty()
    {
        var engine = CreateEngine();

        var forced = engine.ForceConvert();

        Assert.False(forced.HasPlan);
        Assert.Null(forced.Word);
    }

    [Fact]
    public void ForceConvert_ThenUndo_RestoresOriginal()
    {
        var engine = CreateEngine();

        Type(engine, "hello", LayoutCatalog.EnUsId);
        engine.CompleteWord(' ', terminatorAlreadyTyped: true);
        engine.ForceConvert();

        Assert.True(engine.TryUndo(out var undo));
        Assert.NotNull(undo);
        Assert.Equal("руддщ", undo!.CurrentText);
        Assert.Equal("hello", undo.RestoreText);
        Assert.Equal(6, undo.DeleteCount);
        Assert.Equal(LayoutCatalog.RuRuId, undo.CurrentLayoutId);
        Assert.Equal(LayoutCatalog.EnUsId, undo.RestoreLayoutId);
        Assert.Equal(' ', undo.Terminator);
        Assert.False(engine.CanUndo);
    }

    [Fact]
    public void ForceConvert_SecondTime_TogglesBack()
    {
        var engine = CreateEngine();

        Type(engine, "hello", LayoutCatalog.EnUsId);
        engine.CompleteWord(' ', terminatorAlreadyTyped: true);

        var first = engine.ForceConvert();
        Assert.Equal("руддщ", first.Plan!.ReplacementText);

        // Второй двойной Shift — обратно, потому что после первого есть что отменять.
        Assert.True(engine.TryUndo(out var undo));
        Assert.Equal("hello", undo!.RestoreText);
    }

    [Fact]
    public void Detector_ForceConvert_IgnoresThresholdAndMinimumLength()
    {
        var settings = TestEnvironment.DefaultSettings();
        settings.MinWordLength = 4;
        var detector = TestEnvironment.CreateDetector(settings);
        var en = LayoutCatalog.TryGet(LayoutCatalog.EnUsId)!;
        var ru = LayoutCatalog.TryGet(LayoutCatalog.RuRuId)!;

        // «hello» детектор оставляет: слово есть в английском словаре.
        Assert.False(detector.Decide("hello", en).Convert);

        var forced = detector.ForceConvert("hello", en);
        Assert.True(forced.Convert);
        Assert.Equal("руддщ", forced.ConvertedText);
        Assert.Equal(LayoutCatalog.RuRuId, forced.TargetLayoutId);

        var shortWord = detector.ForceConvert("ab", en);
        Assert.True(shortWord.Convert);
        Assert.Equal("фи", shortWord.ConvertedText);

        // Обратное направление тоже работает.
        var back = detector.ForceConvert("руддщ", ru);
        Assert.True(back.Convert);
        Assert.Equal("hello", back.ConvertedText);
    }
}
