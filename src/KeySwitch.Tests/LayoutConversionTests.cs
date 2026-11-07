using KeySwitch.Core.Layout;
using KeySwitch.Tests.TestSupport;
using Xunit;

namespace KeySwitch.Tests;

/// <summary>
/// Конвертация по физическим клавишам. Проверяются обе стороны EN↔RU, регистр,
/// знаки, цифры и обратимость (round-trip).
/// </summary>
public class LayoutConversionTests
{
    private static LayoutTable En => TestEnvironment.En;

    private static LayoutTable Ru => TestEnvironment.Ru;

    [Theory]
    [InlineData("ghbdtn", "привет")]
    [InlineData("rfr", "как")]
    [InlineData("ytn", "нет")]
    [InlineData("ljv", "дом")]
    [InlineData("rf[f", "каха")]
    [InlineData("vbh", "мир")]
    [InlineData("rjyrhtnyj", "конкретно")]
    [InlineData("rjv-gm.nth", "ком-пьютер")]
    public void EnToRu_ByPhysicalKeys(string typed, string expected)
        => Assert.Equal(expected, LayoutConverter.Convert(typed, En, Ru));

    [Theory]
    [InlineData("привет", "ghbdtn")]
    [InlineData("как", "rfr")]
    [InlineData("каха", "rf[f")]
    [InlineData("нет", "ytn")]
    [InlineData("мир", "vbh")]
    [InlineData("ком-пьютер", "rjv-gm.nth")]
    public void RuToEn_ByPhysicalKeys(string typed, string expected)
        => Assert.Equal(expected, LayoutConverter.Convert(typed, Ru, En));

    [Theory]
    [InlineData("Привет", "Ghbdtn")]
    [InlineData("ПРИВЕТ", "GHBDTN")]
    [InlineData("привет", "ghbdtn")]
    public void ShiftLetters_RuToEn(string typed, string expected)
        => Assert.Equal(expected, LayoutConverter.Convert(typed, Ru, En));

    [Theory]
    [InlineData("Ghbdtn", "Привет")]
    [InlineData("GHBDTN", "ПРИВЕТ")]
    [InlineData("ghbdtn", "привет")]
    public void ShiftLetters_EnToRu(string typed, string expected)
        => Assert.Equal(expected, LayoutConverter.Convert(typed, En, Ru));

    /// <summary>
    /// CapsLock в таблице раскладки не участвует: заглавные буквы приходят как Shift.
    /// Физическое нажатие «d» с CapsLock в русской раскладке даёт «В», то есть в конвертации
    /// это upper-case символ — именно его и обрабатывает LayoutConverter.
    /// </summary>
    [Fact]
    public void CapsLock_IsRepresentedAsUpperCaseCharacter()
    {
        // В буфер попадает уже итоговый символ, поэтому конвертер видит «В» и «D».
        Assert.Equal("D", LayoutConverter.Convert("В", Ru, En));
        Assert.Equal("В", LayoutConverter.Convert("D", En, Ru));
    }

    [Theory]
    [InlineData(".", "ю")]
    [InlineData(",", "б")]
    [InlineData(";", "ж")]
    [InlineData("'", "э")]
    [InlineData("[", "х")]
    [InlineData("]", "ъ")]
    [InlineData("`", "ё")]
    [InlineData("/", ".")]
    [InlineData("\\", "\\")]
    [InlineData("@", "\"")]
    [InlineData("#", "№")]
    [InlineData("$", ";")]
    [InlineData("^", ":")]
    [InlineData("&", "?")]
    [InlineData("*", "*")]
    [InlineData("(", "(")]
    [InlineData(")", ")")]
    [InlineData("-", "-")]
    [InlineData("=", "=")]
    [InlineData("+", "+")]
    public void Symbols_EnToRu(string typed, string expected)
        => Assert.Equal(expected, LayoutConverter.Convert(typed, En, Ru));

    [Theory]
    [InlineData("ю", ".")]
    [InlineData("б", ",")]
    [InlineData("ж", ";")]
    [InlineData("э", "'")]
    [InlineData("х", "[")]
    [InlineData("ъ", "]")]
    [InlineData("ё", "`")]
    [InlineData(".", "/")]
    [InlineData("\\", "\\")]
    [InlineData("\"", "@")]
    [InlineData("№", "#")]
    [InlineData(";", "$")]
    [InlineData(":", "^")]
    [InlineData("?", "&")]
    [InlineData("*", "*")]
    [InlineData("(", "(")]
    [InlineData(")", ")")]
    [InlineData("-", "-")]
    [InlineData("=", "=")]
    [InlineData("+", "+")]
    public void Symbols_RuToEn(string typed, string expected)
        => Assert.Equal(expected, LayoutConverter.Convert(typed, Ru, En));

    [Fact]
    public void ShiftedSymbols_PreserveShift()
    {
        // Shift+3 в EN — «#», в RU — «№»; Shift+4 в EN — «$», в RU — «;».
        Assert.Equal("№", LayoutConverter.Convert("#", En, Ru));
        Assert.Equal("$", LayoutConverter.Convert(";", Ru, En));
        Assert.Equal(";", LayoutConverter.Convert("$", En, Ru));
    }

    [Theory]
    [InlineData("0123456789")]
    [InlineData("!")]
    [InlineData("%")]
    [InlineData("*")]
    [InlineData("(")]
    [InlineData(")")]
    public void DigitsAndCommonSymbols_AreStableBothWays(string text)
    {
        Assert.Equal(text, LayoutConverter.Convert(text, En, Ru));
        Assert.Equal(text, LayoutConverter.Convert(text, Ru, En));
    }

    [Fact]
    public void SpaceAndTab_AreStable()
    {
        Assert.Equal(" ", LayoutConverter.Convert(" ", En, Ru));
        Assert.Equal(" ", LayoutConverter.Convert(" ", Ru, En));
        Assert.Equal("ф и", LayoutConverter.Convert("a b", En, Ru));
    }

    [Fact]
    public void UnmappedCharacters_StayUnchanged()
    {
        Assert.Equal("😀", LayoutConverter.Convert("😀", En, Ru));
        Assert.Equal("ё", LayoutConverter.Convert("ё", En, Ru)); // в EN-таблице нет «ё»
        Assert.Equal("漢", LayoutConverter.Convert("漢", Ru, En));
        Assert.Equal("привет😀", LayoutConverter.Convert("ghbdtn😀", En, Ru));
    }

    [Fact]
    public void SameTable_ReturnsSameString()
    {
        const string text = "ghbdtn, мир!";
        Assert.Equal(text, LayoutConverter.Convert(text, En, En));
        Assert.Equal(text, LayoutConverter.Convert(text, Ru, Ru));
    }

    [Fact]
    public void EmptyString_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, LayoutConverter.Convert(string.Empty, En, Ru));
        Assert.Equal(string.Empty, LayoutConverter.Convert(string.Empty, Ru, En));
    }

    [Fact]
    public void CanConvert_And_CountUnmapped()
    {
        Assert.True(LayoutConverter.CanConvert("ghbdtn", En));
        Assert.False(LayoutConverter.CanConvert("привет", En));
        Assert.False(LayoutConverter.CanConvert("ghbdtn", Ru));
        Assert.True(LayoutConverter.CanConvert("привет", Ru));

        Assert.Equal(0, LayoutConverter.CountUnmapped("ghbdtn", En));
        Assert.Equal(6, LayoutConverter.CountUnmapped("привет", En));
        // Суррогатная пара эмодзи — два UTF-16 code unit, оба не отображаются.
        Assert.Equal(2, LayoutConverter.CountUnmapped("abc😀", En));
    }

    /// <summary>Round-trip для всех слов: convert(convert(x)) == x в обе стороны.</summary>
    [Fact]
    public void RoundTrip_AllRussianWords()
    {
        foreach (var word in TestEnvironment.Resources.RuWords)
        {
            var toEn = LayoutConverter.Convert(word, Ru, En);
            var back = LayoutConverter.Convert(toEn, En, Ru);
            Assert.Equal(word, back);
        }
    }

    [Fact]
    public void RoundTrip_AllEnglishWords()
    {
        foreach (var word in TestEnvironment.Resources.EnWords)
        {
            var toRu = LayoutConverter.Convert(word, En, Ru);
            var back = LayoutConverter.Convert(toRu, Ru, En);
            Assert.Equal(word, back);
        }
    }

    /// <summary>Round-trip для всех символов EN-таблицы (включая Shift-варианты).</summary>
    [Fact]
    public void RoundTrip_AllEnglishCharacters()
    {
        foreach (var ch in En.Characters)
        {
            var toRu = LayoutConverter.Convert(ch.ToString(), En, Ru);
            var back = LayoutConverter.Convert(toRu, Ru, En);
            Assert.Equal(ch.ToString(), back);
        }
    }

    /// <summary>«привет,» в RU — это физически «ghbdtn?» в EN (запятая = Shift+«/»).</summary>
    [Fact]
    public void TrailingComma_IsPhysicallyShiftedQuestionMark_InEn()
    {
        Assert.Equal("ghbdtn?", LayoutConverter.Convert("привет,", Ru, En));
        Assert.Equal("привет,", LayoutConverter.Convert("ghbdtn?", En, Ru));
    }

    /// <summary>Точка в EN нажата на клавише, где в RU стоит «ю» (VK 0xBE).</summary>
    [Fact]
    public void Period_IsYu_WhenTypedInEnLayout()
    {
        Assert.Equal("приветю", LayoutConverter.Convert("ghbdtn.", En, Ru));
    }

    /// <summary>«/» в EN стоит на клавише, где в RU — точка (VK 0xBF).</summary>
    [Fact]
    public void Slash_IsPeriod_WhenTypedInEnLayout()
    {
        Assert.Equal("привет.", LayoutConverter.Convert("ghbdtn/", En, Ru));
    }
}
