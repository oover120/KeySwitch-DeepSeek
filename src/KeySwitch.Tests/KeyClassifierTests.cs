using KeySwitch.Core.Input;
using Xunit;

namespace KeySwitch.Tests;

public class KeyClassifierTests
{
    [Theory]
    [InlineData((int)'A', 'a')]
    [InlineData((int)'Z', 'z')]
    [InlineData(0x31, '1')]
    [InlineData(0x39, '0')]
    [InlineData(0xDB, '[')]
    [InlineData(0xDD, ']')]
    [InlineData(0xBA, ';')]
    [InlineData(0xDE, '\'')]
    [InlineData(0xBC, ',')]
    [InlineData(0xBE, '.')]
    [InlineData(0xC0, '`')]
    [InlineData(0x41, 'ф')]
    [InlineData(0x51, 'й')]
    [InlineData(0xBF, 'ю')]
    public void WordChar_ForLettersDigitsAndAmbiguousPunctuation(int vk, char ch)
        => Assert.Equal(KeyRole.WordChar, KeyClassifier.Classify(vk, false, false, false, ch));

    [Theory]
    [InlineData(KeyClassifier.VkSpace, ' ')]
    [InlineData(KeyClassifier.VkReturn, '\r')]
    [InlineData(KeyClassifier.VkTab, '\t')]
    [InlineData(0xBF, '/')]
    [InlineData(0x31, '!')]
    [InlineData(0xBB, '=')]
    public void Terminator_ForSpaceEnterTabAndUnambiguousPunctuation(int vk, char ch)
        => Assert.Equal(KeyRole.Terminator, KeyClassifier.Classify(vk, false, false, false, ch));

    [Theory]
    [InlineData(KeyClassifier.VkBack, null)]
    [InlineData(KeyClassifier.VkBack, 'x')]
    public void Backspace(int vk, char? ch)
        => Assert.Equal(KeyRole.Backspace, KeyClassifier.Classify(vk, false, false, false, ch));

    [Theory]
    [InlineData(KeyClassifier.VkEscape, null)]
    [InlineData(KeyClassifier.VkHome, null)]
    [InlineData(KeyClassifier.VkEnd, null)]
    [InlineData(KeyClassifier.VkLeft, null)]
    [InlineData(KeyClassifier.VkRight, null)]
    [InlineData(KeyClassifier.VkUp, null)]
    [InlineData(KeyClassifier.VkDown, null)]
    [InlineData(KeyClassifier.VkDelete, null)]
    [InlineData(KeyClassifier.VkInsert, null)]
    [InlineData(KeyClassifier.VkPrior, null)]
    [InlineData(KeyClassifier.VkNext, null)]
    [InlineData(KeyClassifier.VkSnapshot, null)]
    public void Reset_ForNavigationAndServiceKeys(int vk, char? ch)
        => Assert.Equal(KeyRole.Reset, KeyClassifier.Classify(vk, false, false, false, ch));

    [Fact]
    public void Pause_IsIgnoredByClassifier_HandledByHooks()
    {
        // Pause обрабатывается отдельно в слое Hooks (ручная конвертация), классификатор его игнорирует.
        Assert.Equal(KeyRole.Ignore, KeyClassifier.Classify(KeyClassifier.VkPause, false, false, false, null));
    }

    [Theory]
    [InlineData(KeyClassifier.VkShift)]
    [InlineData(KeyClassifier.VkControl)]
    [InlineData(KeyClassifier.VkMenu)]
    [InlineData(KeyClassifier.VkCapital)]
    [InlineData(KeyClassifier.VkLWin)]
    [InlineData(KeyClassifier.VkRWin)]
    public void Ignore_ForModifierKeys(int vk)
        => Assert.Equal(KeyRole.Ignore, KeyClassifier.Classify(vk, false, false, false, null));

    [Theory]
    [InlineData((int)'C', 'c', true, false, false)]
    [InlineData((int)'V', 'v', true, false, false)]
    [InlineData((int)'A', 'a', false, true, false)]
    [InlineData((int)'X', 'x', false, false, true)]
    [InlineData((int)'Z', 'z', true, true, false)]
    [InlineData(KeyClassifier.VkLeft, null, true, false, false)]
    public void Reset_ForCtrlAltWinCombinations(int vk, char? ch, bool ctrl, bool alt, bool win)
        => Assert.Equal(KeyRole.Reset, KeyClassifier.Classify(vk, ctrl, alt, win, ch));

    [Fact]
    public void Modifiers_AreIgnored_EvenWithCtrlHeld()
    {
        // Shift/Ctrl/Alt/Caps/Win проверяются раньше ctrl/alt/win-ветки.
        Assert.Equal(KeyRole.Ignore, KeyClassifier.Classify(KeyClassifier.VkShift, true, false, true, null));
        Assert.Equal(KeyRole.Ignore, KeyClassifier.Classify(KeyClassifier.VkControl, true, false, false, null));
        Assert.Equal(KeyRole.Ignore, KeyClassifier.Classify(KeyClassifier.VkCapital, false, false, true, null));
    }

    [Theory]
    [InlineData(0x70)] // F1
    [InlineData(0x7B)] // F12
    [InlineData(0xA0)] // левый Shift в расширенном диапазоне
    public void Ignore_ForUnknownKeyWithoutCharacter(int vk)
        => Assert.Equal(KeyRole.Ignore, KeyClassifier.Classify(vk, false, false, false, null));

    [Fact]
    public void Ignore_ForUnknownKeyWithNullChar()
        => Assert.Equal(KeyRole.Ignore, KeyClassifier.Classify(0x1234, false, false, false, null));

    [Theory]
    [InlineData('a', true)]
    [InlineData('я', true)]
    [InlineData('5', true)]
    [InlineData('[', true)]
    [InlineData('`', true)]
    [InlineData('?', false)]
    [InlineData(' ', false)]
    [InlineData('!', false)]
    public void CanStartWord(char ch, bool expected)
        => Assert.Equal(expected, KeyClassifier.CanStartWord(ch));

    [Theory]
    [InlineData(' ', true)]
    [InlineData('\r', true)]
    [InlineData('\n', true)]
    [InlineData('\t', true)]
    [InlineData('!', true)]
    [InlineData('?', true)]
    [InlineData('a', false)]
    [InlineData('я', false)]
    [InlineData('5', false)]
    [InlineData('[', false)]
    [InlineData('`', false)]
    public void IsTerminatorChar(char ch, bool expected)
        => Assert.Equal(expected, KeyClassifier.IsTerminatorChar(ch));
}
