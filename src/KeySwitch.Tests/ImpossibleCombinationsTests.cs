using KeySwitch.Core.Detect;
using Xunit;

namespace KeySwitch.Tests;

public class ImpossibleCombinationsTests
{
    [Theory]
    [InlineData("ъь")]
    [InlineData("ьъ")]
    [InlineData("ъъ")]
    [InlineData("ьь")]
    [InlineData("ьд")] // начинается с мягкого знака
    [InlineData("ъе")] // начинается с твёрдого знака
    public void Russian_Impossible_ReturnsHighPenalty(string core)
    {
        var (penalty, rule) = ImpossibleCombinations.Check(core, "ru", null);
        Assert.Equal(1.0, penalty);
        Assert.NotNull(rule);
    }

    [Theory]
    [InlineData("подъезд")]
    [InlineData("объявление")]
    [InlineData("съезд")]
    [InlineData("адъютант")]
    [InlineData("объём")]
    [InlineData("привет")]
    [InlineData("всплеск")]
    public void Russian_ValidWords_HaveNoPenalty(string core)
    {
        var (penalty, rule) = ImpossibleCombinations.Check(core, "ru", null);
        Assert.Equal(0.0, penalty);
        Assert.Null(rule);
    }

    [Theory]
    [InlineData("объ")]
    [InlineData("отъ")]
    public void Russian_HardSignOutsidePrefix_Penalty(string core)
    {
        var (penalty, rule) = ImpossibleCombinations.Check(core, "ru", null);
        Assert.Equal(0.6, penalty);
        Assert.NotNull(rule);
    }

    [Theory]
    [InlineData("агентств")]
    public void Russian_LongConsonantRun(string core)
    {
        var (penalty, rule) = ImpossibleCombinations.Check(core, "ru", null);
        Assert.Equal(0.5, penalty);
        Assert.NotNull(rule);
    }

    [Theory]
    [InlineData("qatar")]
    [InlineData("qwerty")]
    public void English_QWithoutU_Penalty(string core)
    {
        var (penalty, rule) = ImpossibleCombinations.Check(core, "en", null);
        Assert.Equal(0.8, penalty);
        Assert.NotNull(rule);
    }

    [Theory]
    [InlineData("queen")]
    [InlineData("quick")]
    [InlineData("q")]
    public void English_QOnlyBeforeU_OrAtEnd_NoPenalty(string core)
    {
        var (penalty, rule) = ImpossibleCombinations.Check(core, "en", null);
        Assert.Equal(0.0, penalty);
        Assert.Null(rule);
    }

    [Theory]
    [InlineData("psst")]
    [InlineData("crwth")]
    public void English_NoVowels_Penalty(string core)
    {
        var (penalty, rule) = ImpossibleCombinations.Check(core, "en", null);
        Assert.Equal(1.0, penalty);
        Assert.NotNull(rule);
    }

    [Theory]
    [InlineData("myth")] // y считается гласной, цепочки согласных ≥5 нет
    [InlineData("rhythm")]
    public void English_ValidWords_NoPenalty(string core)
    {
        var (penalty, rule) = ImpossibleCombinations.Check(core, "en", null);
        Assert.Equal(0.0, penalty);
        Assert.Null(rule);
    }

    [Theory]
    [InlineData("strengths")]
    public void English_ConsonantRun(string core)
    {
        var (penalty, rule) = ImpossibleCombinations.Check(core, "en", null);
        Assert.Equal(0.5, penalty);
        Assert.NotNull(rule);
    }

    [Theory]
    [InlineData("'tis")]
    [InlineData("-word")]
    public void English_StartsWithSign_Penalty(string core)
    {
        var (penalty, rule) = ImpossibleCombinations.Check(core, "en", null);
        Assert.Equal(1.0, penalty);
        Assert.NotNull(rule);
    }

    [Fact]
    public void TooShortEnglish_WithoutVowels_NotPenalized()
    {
        // «hmm» короче 4 — правило «нет гласных» не применяется.
        var (penalty, _) = ImpossibleCombinations.Check("hmm", "en", null);
        Assert.Equal(0.0, penalty);
    }

    [Theory]
    [InlineData("")]
    [InlineData("fr")]
    [InlineData("de")]
    public void EmptyOrUnknownLanguage_NoPenalty(string language)
    {
        var (penalty, rule) = ImpossibleCombinations.Check("ъь", language, null);
        Assert.Equal(0.0, penalty);
        Assert.Null(rule);
    }

    [Fact]
    public void EmptyCore_NoPenalty()
    {
        var (penalty, rule) = ImpossibleCombinations.Check(string.Empty, "ru", null);
        Assert.Equal(0.0, penalty);
        Assert.Null(rule);
    }

    [Fact]
    public void English_ConsonantRun_WithNoVowels_NoVowelsRuleWins()
    {
        var (penalty, rule) = ImpossibleCombinations.Check("psstt", "en", null);
        Assert.Equal(1.0, penalty);
        Assert.Equal("нет ни одной гласной", rule);
    }

    [Fact]
    public void Russian_AllConsonants_LongRun_StillPenalized()
    {
        var (penalty, rule) = ImpossibleCombinations.Check("бвгджзклмн", "ru", null);
        Assert.Equal(0.5, penalty);
        Assert.Equal("слишком длинная цепочка согласных", rule);
    }
}
