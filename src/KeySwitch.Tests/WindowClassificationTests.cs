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
    private static ForegroundWindowInfo Window(
        string processName,
        bool fullscreen,
        string? processPath = null,
        string? className = null) =>
        new(Handle: (IntPtr)1,
            ProcessId: 42,
            ProcessName: processName,
            ProcessPath: processPath ?? $@"C:\Program Files\{processName}.exe",
            LayoutId: "en-US",
            IsElevated: false,
            IsFullscreen: fullscreen,
            IsConsole: false,
            ClassName: className ?? string.Empty);

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

    // ---------------------------------------------------------------- игровые папки

    [Theory]
    [InlineData(@"C:\Games\Dota 2\game\bin\win64\dota2.exe")]
    [InlineData(@"D:\Games\Stalker\bin\xrEngine.exe")]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Portal 2\portal2.exe")]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\CS2\cs2.exe")]
    [InlineData(@"C:\Program Files\Epic Games\Fortnite\FortniteGame.exe")]
    [InlineData(@"C:\Program Files (x86)\GOG Galaxy\Games\Witcher 3\witcher3.exe")]
    [InlineData(@"C:\Program Files (x86)\Battle.net\Game.exe")]
    [InlineData(@"C:\Riot Games\League of Legends\LeagueClient.exe")]
    [InlineData(@"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games\Far Cry\farcry.exe")]
    [InlineData(@"C:\Program Files\EA Games\Battlefield\bf.exe")]
    [InlineData(@"C:\XboxGames\Minecraft\Content\minecraft.exe")]
    [InlineData(@"c:/games/quake/quake.exe")]
    public void IsGameDirectory_GameLibraries_True(string path) =>
        Assert.True(WindowClassification.IsGameDirectory(path));

    [Theory]
    [InlineData(@"C:\Program Files\Notepad\notepad.exe")]
    [InlineData(@"C:\Users\User\AppData\Local\Programs\KeySwitchDeepSeek\KeySwitchDeepSeek.exe")]
    [InlineData(@"C:\Users\User\Documents\work\report.docx.exe")]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    [InlineData(@"C:\Games.zip\not-a-game.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void IsGameDirectory_RegularPaths_False(string? path) =>
        Assert.False(WindowClassification.IsGameDirectory(path));

    // ---------------------------------------------------------------- классы окон движков

    [Theory]
    [InlineData("UnityWndClass")]
    [InlineData("UnrealWindow")]
    [InlineData("SDL_app")]
    [InlineData("GLFW30")]
    [InlineData("CryENGINE")]
    [InlineData("unitywndclass")]
    public void IsGameWindowClass_EngineClasses_True(string className) =>
        Assert.True(WindowClassification.IsGameWindowClass(className));

    [Theory]
    [InlineData("Notepad")]
    [InlineData("Chrome_WidgetWin_1")]
    [InlineData("HwndWrapper[DefaultDomain;;guid]")]
    [InlineData("")]
    [InlineData(null)]
    public void IsGameWindowClass_RegularClasses_False(string? className) =>
        Assert.False(WindowClassification.IsGameWindowClass(className));

    // ---------------------------------------------------------------- итоговое правило

    [Fact]
    public void IsGame_WindowedUnityGameInGamesFolder_True() =>
        Assert.True(WindowClassification.IsGame(
            Window("MyGame", fullscreen: false, processPath: @"C:\Games\MyGame\MyGame.exe")));

    [Fact]
    public void IsGame_WindowedUnityGameOutsideGameFolders_TrueByClass() =>
        Assert.True(WindowClassification.IsGame(
            Window("MyGame", fullscreen: false,
                processPath: @"C:\Program Files\MyGame\MyGame.exe", className: "UnityWndClass")));

    [Fact]
    public void IsGame_FullscreenBrowser_NotAGame() =>
        Assert.False(WindowClassification.IsGame(
            Window("chrome", fullscreen: true, className: "Chrome_WidgetWin_1")));

    [Theory]
    [InlineData("chrome")]
    [InlineData("notepad")]
    [InlineData("devenv")]
    public void IsGame_WindowedRegularApp_False(string processName) =>
        Assert.False(WindowClassification.IsGame(Window(processName, fullscreen: false)));

    [Fact]
    public void IsGame_FullscreenBrowser_False() =>
        Assert.False(WindowClassification.IsGame(Window("chrome", fullscreen: true)));

    [Fact]
    public void IsGame_Null_Throws() =>
        Assert.Throws<ArgumentNullException>(() => WindowClassification.IsGame(null!));
}
