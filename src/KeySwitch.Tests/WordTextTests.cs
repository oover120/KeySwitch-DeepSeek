using KeySwitch.Core.Text;
using Xunit;

namespace KeySwitch.Tests;

public class WordTextTests
{
    [Theory]
    [InlineData("привет,", "привет", ",")]
    [InlineData("hello,", "hello", ",")]
    [InlineData("rf[f", "rf[f", "")] // «[» — буква, а не пунктуация, в хвост не уходит
    [InlineData("привет...", "привет", "...")]
    [InlineData("привет!", "привет", "!")]
    [InlineData("привет?", "привет", "?")]
    [InlineData("hello)", "hello", ")")]
    [InlineData("миру-мир", "миру-мир", "")] // дефис внутри, не в хвосте
    [InlineData("привет ", "привет ", "")] // пробел не считается пунктуацией
    [InlineData("", "", "")]
    public void SplitTrailingPunctuation(string text, string expectedCore, string expectedTail)
    {
        var (core, tail) = WordText.SplitTrailingPunctuation(text);
        Assert.Equal(expectedCore, core);
        Assert.Equal(expectedTail, tail);
    }

    [Fact]
    public void SplitTrailingPunctuation_OnlyPunctuation_ReturnsWholeTextAsCore()
    {
        // Документируем фактическое поведение: если слева нет букв/цифр,
        // текст возвращается целиком, а не пустым ядром.
        var (core, tail) = WordText.SplitTrailingPunctuation("...");
        Assert.Equal("...", core);
        Assert.Equal(string.Empty, tail);
    }

    [Theory]
    [InlineData("ПРИВЕТ", "привет")]
    [InlineData("Ghbdtn", "ghbdtn")]
    [InlineData("Привет", "привет")]
    public void Normalize(string input, string expected)
        => Assert.Equal(expected, WordText.Normalize(input));

    [Theory]
    [InlineData('а', true)]
    [InlineData('ё', true)]
    [InlineData('Ё', true)]
    [InlineData('Я', true)]
    [InlineData('a', false)]
    [InlineData('1', false)]
    public void IsCyrillicLetter(char ch, bool expected)
        => Assert.Equal(expected, WordText.IsCyrillicLetter(ch));

    [Theory]
    [InlineData('a', true)]
    [InlineData('Z', true)]
    [InlineData('я', false)]
    [InlineData('5', false)]
    public void IsLatinLetter(char ch, bool expected)
        => Assert.Equal(expected, WordText.IsLatinLetter(ch));

    [Theory]
    [InlineData('[', true)]
    [InlineData(']', true)]
    [InlineData(';', true)]
    [InlineData('\'', true)]
    [InlineData(',', true)]
    [InlineData('.', true)]
    [InlineData('`', true)]
    [InlineData('!', false)]
    [InlineData('a', false)]
    [InlineData(' ', false)]
    public void IsAmbiguousPunctuation(char ch, bool expected)
        => Assert.Equal(expected, WordText.IsAmbiguousPunctuation(ch));

    [Theory]
    [InlineData('a', true)]
    [InlineData('5', true)]
    [InlineData('я', true)]
    [InlineData('-', false)]
    [InlineData(' ', false)]
    public void IsLetterOrDigit(char ch, bool expected)
        => Assert.Equal(expected, WordText.IsLetterOrDigit(ch));

    [Theory]
    [InlineData("ПРивет", "Привет")]
    [InlineData("ПРИВЕТ", "Привет")]
    [InlineData("привет", "Привет")]
    [InlineData("ghbdtn", "Ghbdtn")]
    [InlineData("", "")]
    [InlineData("a", "A")]
    public void CapitalizeFirst(string input, string expected)
        => Assert.Equal(expected, WordText.CapitalizeFirst(input));
}
