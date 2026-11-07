using System.Buffers.Binary;
using System.IO;
using KeySwitch.Core.Text;
using Xunit;

namespace KeySwitch.Tests;

public class NgramTests
{
    private static Dictionary<ulong, float> SampleEntries() => new()
    {
        [0x0000000000000010UL] = -1.25f,
        [0x00000000000000FFUL] = -0.5f,
        [0x00FF000000000000UL] = -3.75f,
        [0xFFFFFFFFFFFFFFFFUL] = -0.125f,
    };

    private static MemoryStream WriteModel(int order, IEnumerable<KeyValuePair<ulong, float>> entries)
    {
        var stream = new MemoryStream();
        NgramModelFile.Write(stream, order, entries);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void WriteRead_RoundTrip()
    {
        var entries = SampleEntries();
        using var stream = WriteModel(3, entries);
        var model = NgramModelFile.Read(stream);

        Assert.Equal(3, model.Order);
        Assert.Equal(entries.Count, model.Count);
        Assert.Equal(entries.Keys.OrderBy(h => h).ToArray(), model.Hashes);

        foreach (var (hash, prob) in entries)
        {
            var index = model.IndexOf(hash);
            Assert.InRange(index, 0, model.Count - 1);
            Assert.Equal(prob, model.Probabilities[index]);
        }
    }

    [Fact]
    public void Write_SortsByHash_RegardlessOfInputOrder()
    {
        var entries = new[]
        {
            new KeyValuePair<ulong, float>(93, -1f),
            new KeyValuePair<ulong, float>(7, -2f),
            new KeyValuePair<ulong, float>(42, -3f),
        };
        using var stream = WriteModel(4, entries);
        var model = NgramModelFile.Read(stream);
        Assert.Equal(new ulong[] { 7, 42, 93 }, model.Hashes);
    }

    [Fact]
    public void MissingGram_IndexOf_ReturnsMinusOne()
    {
        using var stream = WriteModel(3, SampleEntries());
        var model = NgramModelFile.Read(stream);
        Assert.Equal(-1, model.IndexOf(12345));
        Assert.Equal(-1, model.IndexOf(0));
        Assert.Equal(-1, model.IndexOf(ulong.MaxValue - 1));
    }

    [Fact]
    public void EmptyModel_RoundTrip()
    {
        using var stream = WriteModel(4, Array.Empty<KeyValuePair<ulong, float>>());
        var model = NgramModelFile.Read(stream);
        Assert.Equal(4, model.Order);
        Assert.Equal(0, model.Count);
        Assert.Empty(model.Hashes);
        Assert.Empty(model.Probabilities);
        Assert.Equal(-1, model.IndexOf(1));
    }

    [Fact]
    public void Header_ContainsExpectedMagicAndVersion()
    {
        using var stream = WriteModel(3, SampleEntries());
        var bytes = stream.ToArray();
        Assert.Equal(NgramModelFile.Magic, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)));
        Assert.Equal(new byte[] { 0x4B, 0x53, 0x4E, 0x47 }, bytes[..4]); // "KSNG" в порядке байтов файла
        Assert.Equal(NgramModelFile.Version, bytes[4]);
        Assert.Equal((byte)3, bytes[5]);
        Assert.Equal(16 + SampleEntries().Count * 12, bytes.Length);
    }

    [Fact]
    public void NegativeCount_ThrowsInvalidData()
    {
        using var stream = new MemoryStream(Header(count: -1));
        Assert.Throws<InvalidDataException>(() => NgramModelFile.Read(stream));
    }

    [Fact]
    public void HugeCount_ThrowsInvalidData()
    {
        using var stream = new MemoryStream(Header(count: 20_000_001));
        Assert.Throws<InvalidDataException>(() => NgramModelFile.Read(stream));
    }

    [Fact]
    public void BadMagic_ThrowsInvalidData()
    {
        var bytes = Header();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0xDEADBEEF);
        using var stream = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => NgramModelFile.Read(stream));
    }

    [Fact]
    public void BadVersion_ThrowsInvalidData()
    {
        var bytes = Header();
        bytes[4] = 2;
        using var stream = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => NgramModelFile.Read(stream));
    }

    [Fact]
    public void EmptyStream_ThrowsEndOfStream()
    {
        using var stream = new MemoryStream();
        Assert.Throws<EndOfStreamException>(() => NgramModelFile.Read(stream));
    }

    [Fact]
    public void TruncatedHeader_ThrowsEndOfStream()
    {
        using var stream = new MemoryStream(new byte[10]);
        Assert.Throws<EndOfStreamException>(() => NgramModelFile.Read(stream));
    }

    [Fact]
    public void TruncatedBody_ThrowsEndOfStream()
    {
        var bytes = Header(count: 2);
        var full = new byte[bytes.Length + 12]; // записана только одна запись из двух
        bytes.CopyTo(full, 0);
        using var stream = new MemoryStream(full);
        Assert.Throws<EndOfStreamException>(() => NgramModelFile.Read(stream));
    }

    [Fact]
    public void Hash_IsStableAndDistinguishesWords()
    {
        Assert.Equal(NgramHasher.Hash("привет"), NgramHasher.Hash("привет"));
        Assert.NotEqual(NgramHasher.Hash("привет"), NgramHasher.Hash("привет "));
        Assert.NotEqual(NgramHasher.Hash("привет"), NgramHasher.Hash("превет"));
        Assert.Equal(NgramHasher.Hash(string.Empty), NgramHasher.Hash(string.Empty));
    }

    [Fact]
    public void Pad_AddsBoundaryMarkers_AndLowercases()
    {
        Assert.Equal("^привет$", NgramHasher.Pad("Привет"));
        Assert.Equal("^abc$", NgramHasher.Pad("ABC"));
    }

    [Theory]
    [InlineData("abc", 3, 3)] // ^abc$ → ^ab, abc, bc$
    [InlineData("abc", 4, 2)] // ^abc$ → ^abc, abc$
    [InlineData("", 3, 0)]    // "^$" короче порядка — грамм нет
    public void CountGrams_MatchesPaddedLength(string word, int order, int expected)
        => Assert.Equal(expected, NgramHasher.CountGrams(word, order));

    [Fact]
    public void EnumerateGrams_Count_MatchesCountGrams()
    {
        foreach (var word in new[] { "привет", "hello", "кот", "a", "ёлка" })
        {
            foreach (var order in new[] { 3, 4 })
            {
                var count = NgramHasher.CountGrams(word, order);
                var enumerated = NgramHasher.EnumerateGrams(word, order).Count();
                Assert.Equal(count, enumerated);
            }
        }
    }

    [Fact]
    public void ForEachGram_VisitsAllGrams()
    {
        var grams = new List<ulong>();
        NgramHasher.ForEachGram("hello", 3, grams.Add);
        Assert.Equal(NgramHasher.CountGrams("hello", 3), grams.Count);
        var expected = NgramHasher.EnumerateGrams("hello", 3).Select(NgramHasher.Hash).ToArray();
        Assert.Equal(expected, grams);
    }

    private static byte[] Header(uint magic = NgramModelFile.Magic, byte version = NgramModelFile.Version,
        byte order = 3, int count = 0)
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), magic);
        bytes[4] = version;
        bytes[5] = order;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8, 4), count);
        return bytes;
    }
}
