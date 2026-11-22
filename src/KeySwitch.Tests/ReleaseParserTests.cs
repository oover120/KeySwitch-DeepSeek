using KeySwitch.Core.Updates;
using Xunit;

namespace KeySwitch.Tests;

/// <summary>
/// Разбор ответа GitHub «releases/latest»: версия, ссылка на exe, отсутствие обновления.
/// Сети в тестах нет — проверяется только текст ответа.
/// </summary>
public sealed class ReleaseParserTests
{
    private const string ReleaseJson = """
        {
          "tag_name": "v1.2.1",
          "html_url": "https://github.com/oover120/KeySwitch-DeepSeek/releases/tag/v1.2.1",
          "body": "Двойной Shift принудительно конвертирует слово",
          "assets": [
            { "name": "notes.txt", "browser_download_url": "https://example.test/notes.txt" },
            { "name": "KeySwitchDeepSeek.exe", "browser_download_url": "https://example.test/KeySwitchDeepSeek.exe" }
          ]
        }
        """;

    private const string InstallerUrl = "https://github.com/oover120/KeySwitch-DeepSeek/releases/download/v1.2.8/KeySwitchDeepSeek-1.2.8-setup.exe";
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static readonly string InstallerRelease = $$"""
        {
          "tag_name": "v1.2.8", "draft": false, "prerelease": false,
          "assets": [
            { "name": "KeySwitchDeepSeek.exe", "browser_download_url": "https://example.test/KeySwitchDeepSeek.exe" },
            { "name": "KeySwitchDeepSeek-1.2.8-setup.exe", "state": "uploaded", "size": 50000000,
              "browser_download_url": "{{InstallerUrl}}", "digest": "sha256:{{Digest}}" }
          ]
        }
        """;

    [Fact]
    public void Parse_NewerRelease_ReportsUpdate()
    {
        UpdateInfo info = ReleaseParser.Parse(ReleaseJson, new Version(1, 2, 0));

        Assert.True(info.HasUpdate);
        Assert.Equal("1.2.1", info.LatestVersion);
        Assert.Null(info.Error);
    }

    [Fact]
    public void Parse_SameVersion_NoUpdate()
    {
        UpdateInfo info = ReleaseParser.Parse(ReleaseJson, new Version(1, 2, 1));

        Assert.False(info.HasUpdate);
        Assert.Equal("1.2.1", info.LatestVersion);
    }

    [Fact]
    public void Parse_OlderRelease_NoUpdate()
    {
        UpdateInfo info = ReleaseParser.Parse(ReleaseJson, new Version(2, 0, 0));

        Assert.False(info.HasUpdate);
    }

    [Fact]
    public void Parse_IgnoresBuildRevision()
    {
        UpdateInfo info = ReleaseParser.Parse(ReleaseJson, new Version(1, 2, 1, 99));

        Assert.False(info.HasUpdate);
    }

    [Fact]
    public void Parse_PrefersExecutableAsset()
    {
        UpdateInfo info = ReleaseParser.Parse(ReleaseJson, new Version(1, 0, 0));

        Assert.Equal("https://example.test/KeySwitchDeepSeek.exe", info.DownloadUrl);
        Assert.Equal("https://github.com/oover120/KeySwitch-DeepSeek/releases/tag/v1.2.1", info.ReleaseUrl);
    }

    [Fact]
    public void Parse_SelectsOfficialInstallerEvenWhenPortableExeComesFirst()
    {
        UpdateInfo info = ReleaseParser.Parse(InstallerRelease, new Version(1, 2, 7));

        Assert.Equal(new UpdatePackage(new Uri(InstallerUrl), 50_000_000, Digest), info.Installer);
        Assert.Null(ReleaseParser.Parse(InstallerRelease, new Version(1, 2, 8)).Installer);
    }

    [Theory]
    [InlineData(InstallerUrl, "https://github.com.example.test/oover120/KeySwitch-DeepSeek/releases/download/v1.2.8/KeySwitchDeepSeek-1.2.8-setup.exe")]
    [InlineData("/download/v1.2.8/", "/download/v1.2.7/")]
    [InlineData("\"v1.2.8\"", "\"v1.2.8-beta\"")]
    [InlineData("\"prerelease\": false", "\"prerelease\": true")]
    [InlineData("\"draft\": false", "\"draft\": true")]
    [InlineData("uploaded", "starter")]
    [InlineData("50000000", "0")]
    [InlineData("50000000", "999999999")]
    [InlineData("sha256:" + Digest, "invalid")]
    public void Parse_RejectedInstallerLeavesManualDownloadAvailable(string original, string replacement)
    {
        string release = InstallerRelease.Replace(original, replacement, StringComparison.Ordinal);

        UpdateInfo info = ReleaseParser.Parse(release, new Version(1, 2, 7));

        Assert.True(info.HasUpdate);
        Assert.Null(info.Installer);
    }

    [Fact]
    public void Parse_NoAssets_FallsBackToReleasePage()
    {
        const string json = """
            { "tag_name": "v1.3.0", "html_url": "https://github.com/oover120/KeySwitch-DeepSeek/releases/tag/v1.3.0" }
            """;

        UpdateInfo info = ReleaseParser.Parse(json, new Version(1, 2, 1));

        Assert.True(info.HasUpdate);
        Assert.Equal("https://github.com/oover120/KeySwitch-DeepSeek/releases/tag/v1.3.0", info.DownloadUrl);
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsError()
    {
        UpdateInfo info = ReleaseParser.Parse("{ не json", new Version(1, 0, 0));

        Assert.False(info.HasUpdate);
        Assert.NotNull(info.Error);
    }

    [Fact]
    public void Parse_EmptyJson_ReturnsError()
    {
        UpdateInfo info = ReleaseParser.Parse("   ", new Version(1, 0, 0));

        Assert.False(info.HasUpdate);
        Assert.Equal("пустой ответ сервера", info.Error);
    }

    [Fact]
    public void Parse_NoTag_ReturnsError()
    {
        UpdateInfo info = ReleaseParser.Parse("""{ "html_url": "https://example.test" }""", new Version(1, 0, 0));

        Assert.False(info.HasUpdate);
        Assert.NotNull(info.Error);
    }

    [Fact]
    public void Parse_NotesAreTrimmed()
    {
        string json = $$"""
            { "tag_name": "v1.2.1", "body": "{{new string('x', 900)}}" }
            """;

        UpdateInfo info = ReleaseParser.Parse(json, new Version(1, 0, 0));

        Assert.NotNull(info.Notes);
        Assert.True(info.Notes!.Length <= 701);
        Assert.EndsWith("…", info.Notes);
    }

    [Theory]
    [InlineData("v1.2.1", 1, 2, 1)]
    [InlineData("1.2.1", 1, 2, 1)]
    [InlineData("V2.0", 2, 0, 0)]
    [InlineData("v1.2.1-beta", 1, 2, 1)]
    [InlineData("v1.2.1+build7", 1, 2, 1)]
    [InlineData("  v3.4.5  ", 3, 4, 5)]
    public void ParseTag_ReadsVersion(string tag, int major, int minor, int patch)
    {
        Version? version = ReleaseParser.ParseTag(tag);

        Assert.NotNull(version);
        Assert.Equal(major, version!.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Build);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("v")]
    [InlineData("v1.2.3.4.5")]
    [InlineData("v1.x.0")]
    [InlineData("v-1.2.0")]
    public void ParseTag_RejectsInvalid(string? tag)
    {
        Assert.Null(ReleaseParser.ParseTag(tag));
    }
}
