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
