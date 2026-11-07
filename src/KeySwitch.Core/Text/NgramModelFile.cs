using System.Buffers.Binary;

namespace KeySwitch.Core.Text;

/// <summary>
/// Двоичный формат n-граммных моделей. Один и тот же код используют инструмент сборки
/// данных (запись) и слой данных (чтение), поэтому формат не может разъехаться.
/// Формат v1: "KSNG", version, order, reserved, count, затем count пар (uint64 hash, float log10 p),
/// отсортированных по хешу — чтение идёт бинарным поиском без словаря.
/// </summary>
public static class NgramModelFile
{
    public const uint Magic = 0x474E534B; // "KSNG" little-endian
    public const byte Version = 1;

    public static void Write(Stream stream, int order, IEnumerable<KeyValuePair<ulong, float>> entries)
    {
        var list = entries.ToList();
        list.Sort((a, b) => a.Key.CompareTo(b.Key));

        Span<byte> header = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(header[..4], Magic);
        header[4] = Version;
        header[5] = (byte)order;
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(6, 2), 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(8, 4), list.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(12, 4), 0);
        stream.Write(header);

        var buffer = new byte[12];
        foreach (var pair in list)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(0, 8), pair.Key);
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(8, 4), pair.Value);
            stream.Write(buffer);
        }
    }

    public static NgramModelData Read(Stream stream)
    {
        var header = new byte[16];
        ReadExactly(stream, header);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
        if (magic != Magic)
            throw new InvalidDataException("Неверный заголовок модели n-грамм.");

        var version = header[4];
        if (version != Version)
            throw new InvalidDataException($"Неподдерживаемая версия модели n-грамм: {version}.");

        var order = header[5];
        var count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4));
        if (count < 0 || count > 20_000_000)
            throw new InvalidDataException("Повреждённый файл модели n-грамм.");

        var hashes = new ulong[count];
        var probs = new float[count];
        var buffer = new byte[12];
        for (var i = 0; i < count; i++)
        {
            ReadExactly(stream, buffer);
            hashes[i] = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(0, 8));
            probs[i] = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(8, 4));
        }

        return new NgramModelData(order, hashes, probs);
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read <= 0)
                throw new EndOfStreamException("Неожиданный конец файла модели n-грамм.");
            offset += read;
        }
    }
}

/// <summary>Загруженные данные модели: отсортированные хеши и их log10-вероятности.</summary>
public sealed class NgramModelData
{
    public NgramModelData(int order, ulong[] hashes, float[] probabilities)
    {
        Order = order;
        Hashes = hashes;
        Probabilities = probabilities;
    }

    public int Order { get; }

    public ulong[] Hashes { get; }

    public float[] Probabilities { get; }

    public int Count => Hashes.Length;

    /// <summary>Бинарный поиск хеша. Возвращает индекс или -1.</summary>
    public int IndexOf(ulong hash)
    {
        var lo = 0;
        var hi = Hashes.Length - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            var value = Hashes[mid];
            if (value == hash)
                return mid;
            if (value < hash)
                lo = mid + 1;
            else
                hi = mid - 1;
        }

        return -1;
    }
}
