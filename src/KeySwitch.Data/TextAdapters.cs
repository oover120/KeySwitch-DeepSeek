using System.IO.Compression;
using System.Text;
using KeySwitch.Core.Detect;
using KeySwitch.Core.Text;

namespace KeySwitch.Data;

/// <summary>
/// Адаптер словаря: множество словоформ нижнего регистра + их частоты.
/// Формат источника — строки <c>слово&lt;TAB&gt;частота</c> из <c>words-*.txt.gz</c>,
/// словоформы уже развёрнуты по морфологии на этапе сборки ассетов.
/// В памяти слова лежат одним UTF-8-блобом (см. <see cref="Utf8WordTable"/>): это в разы
/// компактнее <c>Dictionary&lt;string, int&gt;</c> и не создаёт LOH-объектов на слово.
/// </summary>
public sealed class WordDictionary : IWordDictionary
{
    private const int DefaultMostFrequent = 256;

    /// <summary>Сколько элементов топа максимум имеет смысл держать в кэше.</summary>
    private const int MaxCachedMostFrequent = 4096;

    /// <summary>Запас на сжатие при оценке размера UTF-8-блоба (для gzip-ассетов).</summary>
    private const long BlobEstimateFactor = 6;

    private readonly Utf8WordTable _table;
    private readonly int[] _frequencies;
    private string[]? _mostFrequent;

    public WordDictionary(string language, Dictionary<string, int> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        Language = language;

        long bytes = 0;
        foreach (var pair in words)
            bytes += Encoding.UTF8.GetByteCount(pair.Key) + 1;

        var builder = new Utf8WordTableBuilder(words.Count, bytes);
        foreach (var pair in words)
        {
            if (string.IsNullOrEmpty(pair.Key))
                continue;

            builder.AppendRaw(pair.Key, pair.Value);
        }

        _table = builder.Build(out _frequencies);
    }

    internal WordDictionary(string language, Utf8WordTable table, int[] frequencies)
    {
        Language = language;
        _table = table;
        _frequencies = frequencies;
    }

    public string Language { get; }

    public int Count => _table.Count;

    public bool Contains(string word)
    {
        if (string.IsNullOrEmpty(word))
            return false;

        return FindWord(word) >= 0;
    }

    public int GetFrequency(string word)
    {
        if (string.IsNullOrEmpty(word))
            return -1;

        var index = FindWord(word);
        return index >= 0 ? _frequencies[index] : -1;
    }

    /// <summary>Самые частые слова языка (для проверок «топ-100 не добавлять в исключения»).</summary>
    public IReadOnlyList<string> GetMostFrequent(int count)
    {
        if (count <= 0)
            return Array.Empty<string>();

        var cached = Volatile.Read(ref _mostFrequent);
        if (cached is not null && cached.Length >= count)
            return cached.Length == count ? cached : cached[..count];

        var selected = SelectMostFrequent(Math.Max(count, DefaultMostFrequent));
        // Кэшируем только «разумный» топ: запрос всего словаря не должен навсегда оставлять
        // в памяти 400 000 строк (раньше так и происходило при count >= Count).
        if (selected.Length <= MaxCachedMostFrequent)
            Volatile.Write(ref _mostFrequent, selected);
        return Take(selected, count);
    }

    /// <summary>Первые <paramref name="count"/> элементов; count может быть больше длины.</summary>
    private static string[] Take(string[] source, int count)
    {
        var length = Math.Min(count, source.Length);
        return length == source.Length ? source : source[..length];
    }

    /// <summary>Читает gzip-файл словаря (формат ассета).</summary>
    public static WordDictionary ReadGzip(Stream stream, string language)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // Длина сжатого ассета известна заранее: по ней оцениваем размеры буферов, чтобы
        // обойтись одной аллокацией вместо серии LOH-всплесков при росте.
        var compressedLength = 0L;
        if (stream.CanSeek)
            compressedLength = Math.Max(0, stream.Length - stream.Position);

        using var gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
        return ReadInto(gzip, language, compressedLength);
    }

    /// <summary>Читает словарь из текстового потока; терпим к пустым и битым строкам.</summary>
    public static WordDictionary Read(TextReader reader, string language)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var builder = new Utf8WordTableBuilder(1024, 256 * 1024);
        Fill(reader, builder);
        var table = builder.Build(out var frequencies);
        return new WordDictionary(language, table, frequencies);
    }

    private static WordDictionary ReadInto(Stream decompressed, string language, long compressedLength)
    {
        var byteEstimate = compressedLength > 0 ? compressedLength * BlobEstimateFactor : 64 * 1024;
        byteEstimate = Math.Min(byteEstimate, 64L * 1024 * 1024);

        // Одно слово в gzip весит около 3-4 байт, поэтому «сжатые байты / 3» сверху покрывает
        // число слов обоих встроенных словарей; реальный рост остаётся в запасе.
        var wordEstimate = compressedLength > 0 ? (int)Math.Min(compressedLength / 3, 4_000_000) : 1024;
        wordEstimate = Math.Max(wordEstimate, 1024);

        var builder = new Utf8WordTableBuilder(wordEstimate, byteEstimate);
        using (var reader = new StreamReader(
                   decompressed,
                   Encoding.UTF8,
                   detectEncodingFromByteOrderMarks: false,
                   bufferSize: 64 * 1024,
                   leaveOpen: true))
        {
            Fill(reader, builder);
        }

        var table = builder.Build(out var frequencies);
        return new WordDictionary(language, table, frequencies);
    }

    private static void Fill(TextReader reader, Utf8WordTableBuilder builder)
    {
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.AsSpan().Trim();
            if (trimmed.IsEmpty || trimmed[0] == '#')
                continue;

            var tab = trimmed.IndexOf('\t');
            var word = (tab >= 0 ? trimmed[..tab] : trimmed).Trim();
            if (word.IsEmpty)
                continue;

            var frequency = 0;
            if (tab >= 0)
                int.TryParse(trimmed[(tab + 1)..].Trim(), out frequency);

            builder.AppendLowerInvariant(word, frequency);
        }
    }

    private int FindWord(string word)
    {
        var capacity = Utf8Words.LoweredCapacity(word.AsSpan());
        if (capacity <= Utf8Words.StackChars)
        {
            Span<byte> buffer = stackalloc byte[Utf8Words.StackBytes];
            var length = Utf8Words.EncodeLowerInvariant(word.AsSpan(), capacity, buffer);
            return _table.Find(buffer[..length]);
        }

        var heap = new byte[Encoding.UTF8.GetMaxByteCount(capacity)];
        var heapLength = Utf8Words.EncodeLowerInvariant(word.AsSpan(), capacity, heap);
        return _table.Find(heap.AsSpan(0, heapLength));
    }

    /// <summary>
    /// Частичный отбор top-N: куча на N элементов вместо полной сортировки всех слов.
    /// Порядок как раньше — по убыванию частоты, при равенстве — побайтово (Ordinal для BMP).
    /// </summary>
    private string[] SelectMostFrequent(int size)
    {
        var total = _table.Count;
        size = Math.Min(size, total);
        if (size <= 0)
            return Array.Empty<string>();

        int[] heap;
        if (size == total)
        {
            heap = new int[total];
            for (var i = 0; i < total; i++)
                heap[i] = i;
        }
        else
        {
            heap = new int[size];
            var filled = 0;
            for (var index = 0; index < total; index++)
            {
                if (filled < size)
                {
                    heap[filled++] = index;
                    if (filled == size)
                        BuildHeap(heap);
                    continue;
                }

                if (CompareForOutput(index, heap[0]) < 0)
                {
                    heap[0] = index;
                    SiftDown(heap, 0);
                }
            }
        }

        Array.Sort(heap, CompareForOutput);

        var result = new string[heap.Length];
        for (var i = 0; i < heap.Length; i++)
            result[i] = _table.DecodeWord(heap[i]);

        return result;
    }

    /// <summary>Куча-максимум «худших» слов: в корне всегда самый неподходящий кандидат.</summary>
    private void BuildHeap(int[] heap)
    {
        for (var i = (heap.Length / 2) - 1; i >= 0; i--)
            SiftDown(heap, i);
    }

    private void SiftDown(int[] heap, int index)
    {
        var size = heap.Length;
        while (true)
        {
            var left = (2 * index) + 1;
            if (left >= size)
                return;

            var worst = left;
            var right = left + 1;
            if (right < size && CompareForOutput(heap[right], heap[left]) > 0)
                worst = right;

            if (CompareForOutput(heap[index], heap[worst]) >= 0)
                return;

            (heap[index], heap[worst]) = (heap[worst], heap[index]);
            index = worst;
        }
    }

    /// <summary>Сравнение для вывода: отрицательное — левое слово должно идти раньше.</summary>
    private int CompareForOutput(int left, int right)
    {
        var frequency = _frequencies[left].CompareTo(_frequencies[right]);
        return frequency != 0 ? -frequency : _table.CompareWords(left, right);
    }
}

/// <summary>
/// Адаптер n-граммной модели: бинарный поиск хеша и сглаживание — отсутствующие
/// n-граммы получают фиксированное «дно» -6.0 (в файле лежат только присутствующие).
/// </summary>
public sealed class CharNgramModel : ICharNgramModel
{
    /// <summary>log10 вероятности для отсутствующей n-граммы.</summary>
    public const double MissingLogProb = -6.0;

    private readonly NgramModelData _data;

    public CharNgramModel(string language, NgramModelData data)
    {
        Language = language;
        _data = data ?? throw new ArgumentNullException(nameof(data));
    }

    public string Language { get; }

    public int Order => _data.Order;

    public int GramCount => _data.Count;

    /// <summary>Доступ к сырым данным (для оценки и диагностики).</summary>
    public NgramModelData Data => _data;

    public double GetLogProbHash(ulong hash)
    {
        var index = _data.IndexOf(hash);
        return index >= 0 ? _data.Probabilities[index] : MissingLogProb;
    }

    public bool HasGramHash(ulong hash) => _data.IndexOf(hash) >= 0;

    /// <summary>Читает модель формата <see cref="NgramModelFile"/>.</summary>
    public static CharNgramModel Read(Stream stream, string language)
    {
        var data = NgramModelFile.Read(stream);
        return new CharNgramModel(language, data);
    }
}
