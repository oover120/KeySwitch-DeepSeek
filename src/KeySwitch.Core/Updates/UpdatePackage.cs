using System.Security.Cryptography;

namespace KeySwitch.Core.Updates;

public sealed record UpdatePackage(Uri Url, long Size, string Sha256)
{
    public async Task<bool> CopyVerifiedAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        int count;

        while ((count = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            total += count;
            if (total > Size)
                return false;

            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer, 0, count);
        }

        return total == Size && string.Equals(
            Convert.ToHexString(hash.GetHashAndReset()), Sha256, StringComparison.OrdinalIgnoreCase);
    }
}
