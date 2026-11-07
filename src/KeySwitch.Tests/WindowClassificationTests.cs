using KeySwitch.Hooks;
using Xunit;

namespace KeySwitch.Tests;

/// <summary>
/// Правило «полноэкранное окно = игра». Регрессия: окно Chrome в F11 занимает ровно
/// весь монитор и не имеет заголовка, из-за чего автопереключение в браузере
/// выключалось целиком (найдено на живом окне, см. DEVLOG, этап 11).
/// </summary>
public class WindowClassificationTests
{
    private static ForegroundWindowInfo Window(string processName, bool fullscreen) =>
        new(Handle: (IntPtr)1,
            ProcessId: 42,
            ProcessName: processName,
            ProcessPath: $@"C:\Program Files\{processName}.exe",
            LayoutId: "en-US",
            IsElevated: false,
            IsFullscreen: fullscreen,
            IsConsole: false);

    [Theory]
    [InlineData("chrome")]
    [InlineData("msedge")]
    [InlineData("firefox")]
    [InlineData("brave")]
    [InlineData("opera")]
    [InlineData("vivaldi")]
    [InlineData("yandex")]
    [InlineData("Chrome")]
    [InlineData("chrome.exe")]
    public void IsBrowserProcess_KnownBrowsers_True(string processName) =>
        Assert.True(WindowClassification.IsBrowserProcess(processName));

    [Theory]
    [InlineData("notepad")]
    [InlineData("devenv")]
    [InlineData("Code")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsBrowserProcess_OtherOrEmpty_False(string? processName) =>
        Assert.False(WindowClassification.IsBrowserProcess(processName));

    [Theory]
    [InlineData("chrome")]
    [InlineData("msedge")]
    [InlineData("firefox")]
    public void IsFullscreenGame_BrowserFullscreen_False(string processName) =>
        Assert.False(WindowClassification.IsFullscreenGame(Window(processName, fullscreen: true)));

    [Theory]
    [InlineData("notepad")]
    [InlineData("devenv")]
    [InlineData("some-game")]
    public void IsFullscreenGame_OtherFullscreen_True(string processName) =>
        Assert.True(WindowClassification.IsFullscreenGame(Window(processName, fullscreen: true)));

    [Theory]
    [InlineData("chrome")]
    [InlineData("notepad")]
    public void IsFullscreenGame_Windowed_False(string processName) =>
        Assert.False(WindowClassification.IsFullscreenGame(Window(processName, fullscreen: false)));

    [Fact]
    public void IsFullscreenGame_Null_Throws() =>
        Assert.Throws<ArgumentNullException>(() => WindowClassification.IsFullscreenGame(null!));
}
