using KeySwitch.Core.PostProcess;
using KeySwitch.Tests.TestSupport;
using Xunit;

namespace KeySwitch.Tests;

public class PostProcessTests
{
    [Theory]
    [InlineData("ПРивет", true, "Привет")]
    [InlineData("ПРи", true, "При")]
    [InlineData("ПРИВЕТ", false, "ПРИВЕТ")]
    [InlineData("Привет", false, "Привет")]
    [InlineData("привет", false, "привет")]
    [InlineData("ПРиВет", false, "ПРиВет")]
    [InlineData("ПР", false, "ПР")]
    [InlineData("ПРивет!", false, "ПРивет!")]
    public void CapsFixer(string word, bool expectedProcessed, string expectedResult)
    {
        var fixer = new CapsFixer();
        var processed = fixer.TryProcess(word, out var result);
        Assert.Equal(expectedProcessed, processed);
        Assert.Equal(expectedResult, result);
        Assert.Equal("caps", fixer.Id);
    }

    [Fact]
    public void CapsFixer_NameIsCurrent()
    {
        // В коде Name = "ИСправление" (похоже на опечатку, но это метка UI, не логика).
        Assert.Equal("ИСправление", new CapsFixer().Name);
    }

    [Fact]
    public void CapsFixer_AlreadyCorrectWord_ReturnsFalse()
    {
        var fixer = new CapsFixer();
        Assert.False(fixer.TryProcess("Привет", out var result));
        Assert.Equal("Привет", result);
    }

    [Fact]
    public void TypoCorrector_ReplacesLowercase()
    {
        var dictionary = new FakeTypoDictionary();
        dictionary.Map["тоесть"] = "то есть";
        var corrector = new TypoCorrector(dictionary);

        Assert.True(corrector.TryProcess("тоесть", out var result));
        Assert.Equal("то есть", result);
        Assert.Equal("typo", corrector.Id);
    }

    [Theory]
    [InlineData("Тоесть", "То есть")]
    [InlineData("ТОЕСТЬ", "То есть")]
    [InlineData("тоесть", "то есть")]
    public void TypoCorrector_PreservesFirstLetterCase(string word, string expected)
    {
        var dictionary = new FakeTypoDictionary();
        dictionary.Map["тоесть"] = "то есть";
        var corrector = new TypoCorrector(dictionary);

        Assert.True(corrector.TryProcess(word, out var result));
        Assert.Equal(expected, result);
    }

    [Fact]
    public void TypoCorrector_UnknownWord_ReturnsFalse()
    {
        var dictionary = new FakeTypoDictionary();
        dictionary.Map["тоесть"] = "то есть";
        var corrector = new TypoCorrector(dictionary);

        Assert.False(corrector.TryProcess("привет", out var result));
        Assert.Equal("привет", result);
    }

    [Fact]
    public void TypoCorrector_TooShortWord_ReturnsFalse()
    {
        var dictionary = new FakeTypoDictionary();
        dictionary.Map["я"] = "I";
        var corrector = new TypoCorrector(dictionary);

        Assert.False(corrector.TryProcess("я", out _));
    }

    [Fact]
    public void TypoCorrector_ReplacementEqualToWord_ReturnsFalse()
    {
        var dictionary = new FakeTypoDictionary();
        dictionary.Map["привет"] = "привет";
        var corrector = new TypoCorrector(dictionary);

        Assert.False(corrector.TryProcess("привет", out _));
    }

    [Theory]
    [InlineData("зеленый", "зелёный")]
    [InlineData("ежик", "ёжик")]
    public void YoFixer_ReplacesKnownWords(string word, string expected)
    {
        var dictionary = new FakeYoDictionary();
        dictionary.Map["зеленый"] = "зелёный";
        dictionary.Map["ежик"] = "ёжик";
        var fixer = new YoFixer(dictionary);

        Assert.True(fixer.TryProcess(word, out var result));
        Assert.Equal(expected, result);
        Assert.Equal("yo", fixer.Id);
    }

    [Fact]
    public void YoFixer_WordAlreadyWithYo_ReturnsFalse()
    {
        var dictionary = new FakeYoDictionary();
        dictionary.Map["зеленый"] = "зелёный";
        var fixer = new YoFixer(dictionary);

        Assert.False(fixer.TryProcess("зелёный", out var result));
        Assert.Equal("зелёный", result);
        Assert.False(fixer.TryProcess("ЗЕЛЁНЫЙ", out _));
    }

    [Fact]
    public void YoFixer_WordWithoutE_ReturnsFalse()
    {
        var dictionary = new FakeYoDictionary();
        dictionary.Map["молоко"] = "молоко";
        var fixer = new YoFixer(dictionary);

        Assert.False(fixer.TryProcess("молоко", out _));
    }

    [Fact]
    public void YoFixer_UnknownWord_ReturnsFalse()
    {
        var dictionary = new FakeYoDictionary();
        var fixer = new YoFixer(dictionary);

        Assert.False(fixer.TryProcess("зеленый", out _));
    }

    [Fact]
    public void YoFixer_ReplacementLengthMismatch_ReturnsFalse()
    {
        var dictionary = new FakeYoDictionary();
        dictionary.Map["еж"] = "ёжик";
        var fixer = new YoFixer(dictionary);

        Assert.False(fixer.TryProcess("еж", out _));
    }

    [Fact]
    public void YoFixer_TooShortWord_ReturnsFalse()
    {
        var dictionary = new FakeYoDictionary();
        dictionary.Map["е"] = "ё";
        var fixer = new YoFixer(dictionary);

        Assert.False(fixer.TryProcess("е", out _));
    }

    [Fact]
    public void Pipeline_Empty()
    {
        var pipeline = new PostProcessorPipeline(Array.Empty<IPostProcessor>());
        Assert.True(pipeline.IsEmpty);
        Assert.Empty(pipeline.Processors);
        Assert.False(pipeline.TryProcess("привет", out var result, out var id));
        Assert.Equal("привет", result);
        Assert.Equal(string.Empty, id);
    }

    [Fact]
    public void Pipeline_ProcessorsInOrder_FirstMatchWins()
    {
        var first = new FakeProcessor("first", word => word == "слово" ? "слово-1" : null);
        var second = new FakeProcessor("second", word => word == "слово" ? "слово-2" : null);
        var pipeline = new PostProcessorPipeline(new IPostProcessor[] { first, second });

        Assert.False(pipeline.IsEmpty);
        Assert.Equal(2, pipeline.Processors.Count);
        Assert.True(pipeline.TryProcess("слово", out var result, out var id));
        Assert.Equal("слово-1", result);
        Assert.Equal("first", id);
    }

    [Fact]
    public void Pipeline_SecondProcessorWhenFirstDoesNotMatch()
    {
        var first = new FakeProcessor("first", _ => null);
        var second = new FakeProcessor("second", word => word == "слово" ? "слово-2" : null);
        var pipeline = new PostProcessorPipeline(new IPostProcessor[] { first, second });

        Assert.True(pipeline.TryProcess("слово", out var result, out var id));
        Assert.Equal("слово-2", result);
        Assert.Equal("second", id);
    }

    [Fact]
    public void Pipeline_NoMatch_ReturnsInput()
    {
        var first = new FakeProcessor("first", _ => null);
        var pipeline = new PostProcessorPipeline(new IPostProcessor[] { first });

        Assert.False(pipeline.TryProcess("слово", out var result, out var id));
        Assert.Equal("слово", result);
        Assert.Equal(string.Empty, id);
    }

    [Fact]
    public void RealPipeline_TypoBeforeYo()
    {
        var typos = new FakeTypoDictionary();
        typos.Map["зеленый"] = "зелёный";
        var yo = new FakeYoDictionary();
        yo.Map["зеленый"] = "зелёный";

        var pipeline = new PostProcessorPipeline(new IPostProcessor[]
        {
            new TypoCorrector(typos),
            new YoFixer(yo),
        });

        Assert.True(pipeline.TryProcess("зеленый", out var result, out var id));
        Assert.Equal("зелёный", result);
        Assert.Equal("typo", id);
    }

    [Fact]
    public void RealPipeline_CapsBeforeOthers()
    {
        var typos = new FakeTypoDictionary();
        typos.Map["привет"] = "приветик";
        var pipeline = new PostProcessorPipeline(new IPostProcessor[]
        {
            new CapsFixer(),
            new TypoCorrector(typos),
        });

        Assert.True(pipeline.TryProcess("ПРивет", out var result, out var id));
        Assert.Equal("Привет", result);
        Assert.Equal("caps", id);
    }

    private sealed class FakeProcessor : IPostProcessor
    {
        private readonly Func<string, string?> _func;

        public FakeProcessor(string id, Func<string, string?> func)
        {
            Id = id;
            _func = func;
        }

        public string Id { get; }

        public string Name => Id;

        public bool TryProcess(string word, out string result)
        {
            var replacement = _func(word);
            result = replacement ?? word;
            return replacement != null;
        }
    }
}
