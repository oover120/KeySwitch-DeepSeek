using System.IO;
using System.Security.Cryptography;
using KeySwitch.Core.Updates;
using Xunit;

namespace KeySwitch.Tests;

public sealed class UpdatePackageTests
{
    [Theory]
    [InlineData(3, false, true)]
    [InlineData(3, true, false)]
    [InlineData(2, false, false)]
    [InlineData(4, false, false)]
    public async Task CopyVerifiedAsync_RequiresExactLengthAndDigest(int expectedSize, bool corrupted, bool valid)
    {
        byte[] actual = corrupted ? "xyz"u8.ToArray() : "abc"u8.ToArray();
        var package = new UpdatePackage(new Uri("https://example.test/setup.exe"), expectedSize,
            Convert.ToHexString(SHA256.HashData("abc"u8)).ToLowerInvariant());
        using var source = new MemoryStream(actual);
        using var destination = new MemoryStream();

        Assert.Equal(valid, await package.CopyVerifiedAsync(source, destination, CancellationToken.None));
        Assert.True(destination.Length <= expectedSize);
        if (valid)
            Assert.Equal(actual, destination.ToArray());
    }
}
