using KeySwitch.Core.Detect;
using KeySwitch.Core.Engine;
using KeySwitch.Core.Input;
using KeySwitch.Core.Layout;
using KeySwitch.Core.PostProcess;
using KeySwitch.Tests.TestSupport;
using Xunit;

namespace KeySwitch.Tests;

/// <summary>
/// Однобуквенные слова: «привет малыш b карлсон» — одиночная латинская «b», набранная в русской
/// раскладке, должна стать «и». Правило узкое: «a» и «i» (однобуквенные слова английского)
/// остаются как набраны, а конвертация идёт только в однобуквенное слово целевого языка.
/// </summary>
public class SingleLetterTests
{
    private static readonly LayoutDetector Detector = TestEnvironment.CreateDetector();

    [Theory]
    [InlineData("b", "и")]
    [InlineData("c", "с")]
    [InlineData("z", "я")]
    [InlineData("d", "в")]
    public void LatinLetter_TypedInRussianContext_BecomesRussianOneLetterWord(string typed, string expected)
    {
        var result = Detector.Decide(typed, TestEnvironment.En);

        Assert.True(result.Convert, $"«{typed}» не сконвертировалась: {result.Reason}");
        Assert.Equal(expected, result.ConvertedText);
        Assert.Equal(LayoutCatalog.RuRuId, result.TargetLayoutId);
        Assert.Contains("однобуквенное слово", result.Reason);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("i")]
    public void EnglishOneLetterWords_AreKept(string typed)
    {
        var result = Detector.Decide(typed, TestEnvironment.En);

        Assert.False(result.Convert);
        Assert.Equal("однобуквенное слово исходного языка", result.Reason);
    }

    [Theory]
    [InlineData("и")]
    [InlineData("в")]
    [InlineData("я")]
    public void RussianOneLetterWords_TypedInRussian_AreKept(string typed)
    {
        var result = Detector.Decide(typed, TestEnvironment.Ru);

        Assert.False(result.Convert);
        Assert.Equal("однобуквенное слово исходного языка", result.Reason);
    }

    [Theory]
    [InlineData("y")]   // «н» — не слово
    [InlineData("q")]   // «й» — не слово
    [InlineData("w")]   // «ц» — не слово
    public void LatinLettersWithoutRussianOneLetterWord_AreKept(string typed)
    {
        var result = Detector.Decide(typed, TestEnvironment.En);

        Assert.False(result.Convert);
        Assert.Equal("нет однобуквенного слова в целевой раскладке", result.Reason);
    }

    [Fact]
    public void SingleLetterWithPunctuation_IsKept()
    {
        var result = Detector.Decide("b,", TestEnvironment.En);

        Assert.False(result.Convert);
        Assert.Equal("слово слишком короткое", result.Reason);
    }

    [Fact]
    public void MinimumWordLengthSetting_DoesNotBlockSingleLetter()
    {
        var settings = TestEnvironment.DefaultSettings();
        settings.MinWordLength = 5;
        var detector = TestEnvironment.CreateDetector(settings);

        var result = detector.Decide("b", TestEnvironment.En);

        Assert.True(result.Convert);
        Assert.Equal("и", result.ConvertedText);
    }

    [Fact]
    public void ForceConvert_WorksForSingleLetter()
    {
        var result = Detector.ForceConvert("b", TestEnvironment.En);

        Assert.True(result.Convert);
        Assert.Equal("и", result.ConvertedText);
        Assert.StartsWith("принудительно", result.Reason);
    }

    [Fact]
    public void ForceConvert_SingleLetterWithoutDictionaryWord()
    {
        var result = Detector.ForceConvert("q", TestEnvironment.En);

        Assert.True(result.Convert);
        Assert.Equal("й", result.ConvertedText);
    }

    [Fact]
    public void ForceConvert_IgnoresMinimumWordLength()
    {
        var settings = TestEnvironment.DefaultSettings();
        settings.MinWordLength = 6;
        var detector = TestEnvironment.CreateDetector(settings);

        var result = detector.ForceConvert("b", TestEnvironment.En);

        Assert.True(result.Convert);
        Assert.Equal("и", result.ConvertedText);
    }

    [Fact]
    public void ForceConvert_TwoLettersStillWorks()
    {
        var result = Detector.ForceConvert("gh", TestEnvironment.En);

        Assert.True(result.Convert);
        Assert.Equal("пр", result.ConvertedText);
    }

    [Fact]
    public void UppercaseSingleLetter_KeepsCase()
    {
        var result = Detector.Decide("B", TestEnvironment.En);

        Assert.True(result.Convert);
        Assert.Equal("И", result.ConvertedText);
    }

    [Fact]
    public void SingleLetterInText_IsConvertedByEngine()
    {
        var settings = TestEnvironment.DefaultSettings();
        var engine = new CorrectionEngine(
            TestEnvironment.CreateDetector(settings),
            settings,
            new FakeLearningStore(),
            new FakeExceptionPolicy(),
            new PostProcessorPipeline(Array.Empty<IPostProcessor>()));

        engine.AppendChar(new KeyStroke(0x42, false, false), 'b', LayoutCatalog.EnUsId);
        var result = engine.CompleteWord(' ', terminatorAlreadyTyped: true);

        Assert.True(result.HasPlan);
        Assert.Equal("и", result.Plan!.ReplacementText);
        Assert.Equal(2, result.Plan.DeleteCount);
    }
}
