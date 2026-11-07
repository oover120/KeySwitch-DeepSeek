using System.IO.Compression;
using System.Text;
using KeySwitch.Core.PostProcess;

namespace KeySwitch.Data;

/// <summary>
/// Встроенный словарь однозначных «ё»-слов: «зеленый» → «зелёный». Пары строятся на этапе
/// сборки данных только для случаев, когда ё-вариант есть в словаре, а е-варианта нет,
/// поэтому замена не меняет смысл. Словарь только для чтения и не требует пользовательских файлов.
/// Пары хранятся компактно: ключи и значения — UTF-8-блобы с int-смещениями
/// (см. <see cref="Utf8WordTable"/>), без <c>Dictionary&lt;string, string&gt;</c> на 30 000 пар.
/// </summary>
public sealed class YoDictionary : IYoDictionary
{
    /// <summary>Имя встроенного ассета.</summary>
    public const string AssetName = "yo-ru.txt.gz";

    private readonly Utf8WordTable _keys = Utf8WordTable.Empty;
    private readonly Utf8WordTable _values = Utf8WordTable.Empty;
    private readonly int[] _valueIndexes = Array.Empty<int>();

    public YoDictionary(AppLogger? logger = null)
    {
        try
        {
            using var stream = EmbeddedAssets.Open(AssetName);
            if (stream is null)
            {
                logger?.Warn($"Встроенный словарь ё-слов не найден ({AssetName}).");
                return;
            }

            var compressedLength = stream.CanSeek ? Math.Max(0, stream.Length - stream.Position) : 0;
            using var gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: false);
            using var reader = new StreamReader(gzip, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);

            var keyBuilder = new Utf8WordTableBuilder(32 * 1024, compressedLength > 0 ? compressedLength * 6 : 256 * 1024);
            var valueBuilder = new Utf8WordTableBuilder(32 * 1024, compressedLength > 0 ? compressedLength * 4 : 128 * 1024, sort: false, buildPrefix: false);
            var valueIndexes = new List<int>(32 * 1024);

            while (reader.ReadLine() is { } line)
            {
                var trimmed = line.AsSpan().Trim();
                if (trimmed.IsEmpty || trimmed[0] == '#')
                    continue;

                var tab = trimmed.IndexOf('\t');
                if (tab <= 0 || tab >= trimmed.Length - 1)
                    continue;

                var withoutYo = trimmed[..tab].Trim();
                var withYo = trimmed[(tab + 1)..].Trim();
                if (withoutYo.IsEmpty || withYo.IsEmpty || withoutYo.SequenceEqual(withYo))
                    continue;

                // Значение кодируется на каждую строку; дубликат ключа (если вдруг попадётся)
                // перезаписывает ссылку на значение — как indexer обычного словаря.
                var valueIndex = valueBuilder.Count;
                valueBuilder.AppendLowerInvariant(withYo, valueIndex);
                var keyIndex = keyBuilder.AppendLowerInvariant(withoutYo, valueIndex);
                if (keyIndex < 0)
                    continue;

                if (keyIndex < valueIndexes.Count)
                    valueIndexes[keyIndex] = valueIndex;
                else
                    valueIndexes.Add(valueIndex);
            }

            _keys = keyBuilder.Build(out _);
            _values = valueBuilder.Build(out _);
            _valueIndexes = valueIndexes.ToArray();
            logger?.Info($"Словарь ё-слов загружен: пар {_keys.Count}.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            logger?.Error("Не удалось загрузить встроенный словарь ё-слов.", ex);
        }
    }

    public int Count => _keys.Count;

    public bool TryGetYoWord(string word, out string yoWord)
    {
        yoWord = string.Empty;
        if (string.IsNullOrWhiteSpace(word))
            return false;

        var trimmed = word.Trim();
        if (trimmed.Length == 0)
            return false;

        var capacity = Utf8Words.LoweredCapacity(trimmed.AsSpan());
        int keyIndex;
        if (capacity <= Utf8Words.StackChars)
        {
            Span<byte> buffer = stackalloc byte[Utf8Words.StackBytes];
            var length = Utf8Words.EncodeLowerInvariant(trimmed.AsSpan(), capacity, buffer);
            keyIndex = _keys.Find(buffer[..length]);
        }
        else
        {
            var heap = new byte[Encoding.UTF8.GetMaxByteCount(capacity)];
            var length = Utf8Words.EncodeLowerInvariant(trimmed.AsSpan(), capacity, heap);
            keyIndex = _keys.Find(heap.AsSpan(0, length));
        }

        if (keyIndex < 0 || keyIndex >= _valueIndexes.Length)
            return false;

        var valueIndex = _valueIndexes[keyIndex];
        if (valueIndex < 0 || valueIndex >= _values.Count)
            return false;

        yoWord = _values.DecodeWord(valueIndex);
        return true;
    }
}
