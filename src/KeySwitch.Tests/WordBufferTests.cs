using KeySwitch.Core.Input;
using KeySwitch.Core.Layout;
using Xunit;

namespace KeySwitch.Tests;

public class WordBufferTests
{
    private static KeyStroke Stroke(int vk, bool shift = false, bool caps = false, int scan = 0, bool extended = false)
        => new(vk, shift, caps, scan, extended);

    [Fact]
    public void EmptyBuffer_Defaults()
    {
        var buffer = new WordBuffer();
        Assert.True(buffer.IsEmpty);
        Assert.Equal(0, buffer.Length);
        Assert.Equal(string.Empty, buffer.Text);
        Assert.Empty(buffer.Strokes);
        Assert.Equal(LayoutCatalog.EnUsId, buffer.LayoutId);
    }

    [Fact]
    public void Append_AccumulatesTextAndStrokes()
    {
        var buffer = new WordBuffer();
        buffer.Append(Stroke(0x47, false, false, 34), 'п', LayoutCatalog.RuRuId);
        buffer.Append(Stroke(0x48, false, false, 35), 'р', LayoutCatalog.RuRuId);
        buffer.Append(Stroke(0x41, false, false, 30), 'ф', LayoutCatalog.RuRuId);

        Assert.False(buffer.IsEmpty);
        Assert.Equal(3, buffer.Length);
        Assert.Equal("прф", buffer.Text);
        Assert.Equal('п', buffer[0]);
        Assert.Equal('р', buffer[1]);
        Assert.Equal('ф', buffer[2]);
        Assert.Equal(3, buffer.Strokes.Count);
        Assert.Equal(0x47, buffer.Strokes[0].VirtualKey);
        Assert.Equal(34, buffer.Strokes[0].ScanCode);
    }

    [Fact]
    public void LayoutId_ComesFromFirstAppend_AndIgnoresLater()
    {
        var buffer = new WordBuffer();
        buffer.Append(Stroke(0x50), 'з', LayoutCatalog.RuRuId);
        buffer.Append(Stroke(0x4C), 'д', LayoutCatalog.EnUsId); // раскладку сменили посреди слова
        Assert.Equal(LayoutCatalog.RuRuId, buffer.LayoutId);
    }

    [Fact]
    public void Clear_ResetsContent_ButKeepsLayoutId()
    {
        var buffer = new WordBuffer();
        buffer.Append(Stroke(0x50), 'з', LayoutCatalog.RuRuId);
        buffer.Clear();

        Assert.True(buffer.IsEmpty);
        Assert.Equal(0, buffer.Length);
        Assert.Equal(string.Empty, buffer.Text);
        Assert.Empty(buffer.Strokes);
        // Документируем фактическое поведение: LayoutId после Clear сохраняется до первого Append.
        Assert.Equal(LayoutCatalog.RuRuId, buffer.LayoutId);
    }

    [Fact]
    public void Clear_ThenAppend_SetsNewLayout()
    {
        var buffer = new WordBuffer();
        buffer.Append(Stroke(0x50), 'з', LayoutCatalog.RuRuId);
        buffer.Clear();
        buffer.Append(Stroke(0x41), 'a', LayoutCatalog.EnUsId);
        Assert.Equal(LayoutCatalog.EnUsId, buffer.LayoutId);
        Assert.Equal("a", buffer.Text);
    }

    [Fact]
    public void Backspace_RemovesLastCharAndStroke()
    {
        var buffer = new WordBuffer();
        buffer.Append(Stroke(0x41), 'a', LayoutCatalog.EnUsId);
        buffer.Append(Stroke(0x42), 'b', LayoutCatalog.EnUsId);

        Assert.True(buffer.Backspace());
        Assert.Equal("a", buffer.Text);
        Assert.Equal(1, buffer.Length);
        Assert.Single(buffer.Strokes);

        Assert.True(buffer.Backspace());
        Assert.True(buffer.IsEmpty);
        Assert.Equal(string.Empty, buffer.Text);
        Assert.Empty(buffer.Strokes);

        Assert.False(buffer.Backspace());
        Assert.True(buffer.IsEmpty);
    }

    [Fact]
    public void Backspace_OnEmpty_ReturnsFalse_AndKeepsLayoutId()
    {
        var buffer = new WordBuffer();
        buffer.Append(Stroke(0x50), 'з', LayoutCatalog.RuRuId);
        buffer.Clear();
        Assert.False(buffer.Backspace());
        Assert.Equal(LayoutCatalog.RuRuId, buffer.LayoutId);
    }

    [Fact]
    public void ToToken_SnapshotsState()
    {
        var buffer = new WordBuffer();
        buffer.Append(Stroke(0x47, false, false, 34), 'п', LayoutCatalog.RuRuId);
        buffer.Append(Stroke(0x48, true, true, 35, false), 'Р', LayoutCatalog.RuRuId);

        var token = buffer.ToToken();
        Assert.Equal("пР", token.Text);
        Assert.Equal(LayoutCatalog.RuRuId, token.LayoutId);
        Assert.Equal(2, token.Strokes.Count);
        Assert.True(token.Strokes[1].Shift);
        Assert.True(token.Strokes[1].CapsLock);

        // Последующие изменения буфера не меняют снятый токен.
        buffer.Append(Stroke(0x41), 'ф', LayoutCatalog.RuRuId);
        buffer.Backspace();
        buffer.Clear();
        Assert.Equal("пР", token.Text);
        Assert.Equal(2, token.Strokes.Count);
    }

    [Fact]
    public void ToToken_OnEmpty_ReturnsEmptyText_WithCurrentLayout()
    {
        var buffer = new WordBuffer();
        var token = buffer.ToToken();
        Assert.Equal(string.Empty, token.Text);
        Assert.Empty(token.Strokes);
        Assert.Equal(LayoutCatalog.EnUsId, token.LayoutId);
    }

    [Fact]
    public void ToToken_AfterClear_KeepsCurrentLayout()
    {
        var buffer = new WordBuffer();
        buffer.Append(Stroke(0x50), 'з', LayoutCatalog.RuRuId);
        buffer.Clear();
        Assert.Equal(LayoutCatalog.RuRuId, buffer.ToToken().LayoutId);
    }
}
