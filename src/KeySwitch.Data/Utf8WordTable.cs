using System.Text;

namespace KeySwitch.Data;

/// <summary>
/// Компактная таблица словоформ: все слова склеены в один UTF-8-блоб, границы слов лежат в
/// <c>int[]</c>, ключи отсортированы побайтово. Поиск — бинарный, сравнение — побайтовое,
/// строка на каждую проверку не создаётся. Таблица неизменяема после загрузки, поэтому
/// чтение из нескольких потоков безопасно без блокировок.
/// </summary>
internal sealed class Utf8WordTable
{
    private readonly byte[] _blob;
    private readonly int[] _offsets;

    /// <summary>
    /// Начала диапазонов по первым двум байтам слова (с дополнением нулём): <c>_prefix[p]</c> —
    /// первый индекс слова с префиксом ≥ <c>p</c>, <c>_prefix[65536] == Count</c>. Позволяет
    /// сузить бинарный поиск с сотен тысяч слов до сотен, не храня сами слова дважды.
    /// </summary>
    private readonly int[]? _prefix;

    /// <summary>Пустая таблица (ассет не найден).</summary>
    public static readonly Utf8WordTable Empty = new(Array.Empty<byte>(), new[] { 0 }, 0, buildPrefix: false);

    public Utf8WordTable(byte[] blob, int[] offsets, int count, bool buildPrefix = true)
    {
        _blob = blob ?? throw new ArgumentNullException(nameof(blob));
        _offsets = offsets ?? throw new ArgumentNullException(nameof(offsets));
        Count = count;
        if (buildPrefix && count > 0)
            _prefix = BuildPrefixTable();
    }

    public int Count { get; }

    /// <summary>Байты слова по индексу (без копирования).</summary>
    public ReadOnlySpan<byte> GetWord(int index) =>
        _blob.AsSpan(_offsets[index], _offsets[index + 1] - _offsets[index]);

    /// <summary>Индекс слова в таблице или -1.</summary>
    public int Find(ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty || Count == 0)
            return -1;

        int low = 0;
        var high = Count - 1;
        if (_prefix is not null)
        {
            var bucket = PrefixOf(key);
            low = _prefix[bucket];
            high = _prefix[bucket + 1] - 1;
        }

        while (low <= high)
        {
            var middle = low + ((high - low) >> 1);
            var comparison = GetWord(middle).SequenceCompareTo(key);
            if (comparison == 0)
                return middle;
            if (comparison < 0)
                low = middle + 1;
            else
                high = middle - 1;
        }

        return -1;
    }

    private int[] BuildPrefixTable()
    {
        var prefix = new int[65537];
        for (var i = 0; i < Count; i++)
            prefix[PrefixOf(GetWord(i))]++;

        var sum = 0;
        for (var p = 0; p < 65536; p++)
        {
            var count = prefix[p];
            prefix[p] = sum;
            sum += count;
        }

        prefix[65536] = sum;
        return prefix;
    }

    /// <summary>Первые два байта слова как 16-битный префикс; короткое слово дополняется нулём.</summary>
    private static int PrefixOf(ReadOnlySpan<byte> word) => word.Length switch
    {
        0 => -1,
        1 => word[0] << 8,
        _ => (word[0] << 8) | word[1],
    };

    /// <summary>Строка по индексу; аллоцирует только при явном запросе значения.</summary>
    public string DecodeWord(int index) =>
        Encoding.UTF8.GetString(_blob, _offsets[index], _offsets[index + 1] - _offsets[index]);

    /// <summary>Побайтовое сравнение двух слов таблицы (порядок UTF-8).</summary>
    public int CompareWords(int left, int right) => GetWord(left).SequenceCompareTo(GetWord(right));
}

/// <summary>
/// Вспомогательные методы кодирования слов в UTF-8. Для коротких слов (типичный случай)
/// буферы берутся со стека, поэтому проверка слова не создаёт управляемых аллокаций.
/// </summary>
internal static class Utf8Words
{
    /// <summary>До этой длины слова буферы выделяются на стеке.</summary>
    internal const int StackChars = 64;

    /// <summary>Верхняя граница UTF-8 для <see cref="StackChars"/> символов плюс запас.</summary>
    internal const int StackBytes = (StackChars * 3) + 3;

    /// <summary>
    /// Длина буфера для слова в нижнем регистре. <c>ToLowerInvariant</c> раскрывает
    /// единственный символ — U+0130 («İ») — в два символа; для него резервируем место.
    /// </summary>
    public static int LoweredCapacity(ReadOnlySpan<char> word)
    {
        var capacity = word.Length;
        foreach (var c in word)
        {
            if (c == '\u0130')
                capacity++;
        }

        return capacity;
    }

    /// <summary>Кодирует слово в нижнем регистре инвариантной культуры в UTF-8.</summary>
    public static int EncodeLowerInvariant(ReadOnlySpan<char> word, int loweredCapacity, Span<byte> destination)
    {
        if (loweredCapacity <= StackChars)
        {
            Span<char> lowered = stackalloc char[StackChars];
            var stackLength = MemoryExtensions.ToLowerInvariant(word, lowered);
            return Encoding.UTF8.GetBytes(lowered[..stackLength], destination);
        }

        var heapLowered = new char[loweredCapacity];
        var heapLength = MemoryExtensions.ToLowerInvariant(word, heapLowered);
        return Encoding.UTF8.GetBytes(heapLowered[..heapLength], destination);
    }

    /// <summary>Кодирует слово как есть (без нормализации регистра).</summary>
    public static int EncodeRaw(ReadOnlySpan<char> word, Span<byte> destination) =>
        Encoding.UTF8.GetBytes(word, destination);
}

/// <summary>
/// Сборщик <see cref="Utf8WordTable"/>: накапливает слова, проверяя, что вход уже отсортирован
/// побайтово. Если порядок нарушен (произвольный <see cref="TextReader"/>), таблица один раз
/// пересобирается через массив индексов — без массива строк и без повторного парсинга.
/// </summary>
internal sealed class Utf8WordTableBuilder
{
    private byte[] _blob;
    private int[] _offsets;
    private int[] _values;
    private int _blobLength;
    private int _count;
    private readonly bool _sort;
    private readonly bool _buildPrefix;
    private bool _sorted = true;

    public Utf8WordTableBuilder(int capacity, long expectedBytes, bool sort = true, bool buildPrefix = true)
    {
        if (capacity < 0)
            capacity = 0;
        if (expectedBytes < 16)
            expectedBytes = 16;
        if (expectedBytes > Array.MaxLength)
            expectedBytes = Array.MaxLength;

        _blob = new byte[expectedBytes];
        _offsets = new int[capacity + 1];
        _values = new int[capacity];
        _sort = sort;
        _buildPrefix = buildPrefix;
    }

    /// <summary>Сколько слов уже принято (без дубликатов, если включена сортировка).</summary>
    public int Count => _count;

    /// <summary>
    /// Добавляет слово. Возвращает индекс слова в таблице или -1 для пустого слова.
    /// При включённой сортировке дубликат не добавляется: побеждает последнее значение
    /// (как у <c>Dictionary&lt;string, int&gt;</c>).
    /// </summary>
    public int Append(ReadOnlySpan<byte> word, int value)
    {
        if (word.IsEmpty)
            return -1;

        if (_sort && _count > 0 && _sorted)
        {
            var comparison = GetWord(_count - 1).SequenceCompareTo(word);
            if (comparison == 0)
            {
                _values[_count - 1] = value;
                return _count - 1;
            }

            if (comparison > 0)
                _sorted = false;
        }

        EnsureWordSlots();
        EnsureBlob(word.Length);

        word.CopyTo(_blob.AsSpan(_blobLength));
        _blobLength += word.Length;
        _offsets[_count + 1] = _blobLength;
        _values[_count] = value;
        return _count++;
    }

    /// <summary>Добавляет слово из UTF-16, приводя его к нижнему регистру.</summary>
    public int AppendLowerInvariant(ReadOnlySpan<char> word, int value)
    {
        if (word.IsEmpty)
            return -1;

        var capacity = Utf8Words.LoweredCapacity(word);
        if (capacity <= Utf8Words.StackChars)
        {
            Span<byte> buffer = stackalloc byte[Utf8Words.StackBytes];
            var length = Utf8Words.EncodeLowerInvariant(word, capacity, buffer);
            return Append(buffer[..length], value);
        }

        var heap = new byte[Encoding.UTF8.GetMaxByteCount(capacity)];
        var heapLength = Utf8Words.EncodeLowerInvariant(word, capacity, heap);
        return Append(heap.AsSpan(0, heapLength), value);
    }

    /// <summary>Добавляет слово из UTF-16 без изменения регистра.</summary>
    public int AppendRaw(ReadOnlySpan<char> word, int value)
    {
        if (word.IsEmpty)
            return -1;

        var maxBytes = Encoding.UTF8.GetMaxByteCount(word.Length);
        if (maxBytes <= Utf8Words.StackBytes)
        {
            Span<byte> buffer = stackalloc byte[Utf8Words.StackBytes];
            var length = Utf8Words.EncodeRaw(word, buffer);
            return Append(buffer[..length], value);
        }

        var heap = new byte[maxBytes];
        var heapLength = Utf8Words.EncodeRaw(word, heap);
        return Append(heap.AsSpan(0, heapLength), value);
    }

    /// <summary>Завершает сборку: при необходимости сортирует и обрезает буферы.</summary>
    public Utf8WordTable Build(out int[] values)
    {
        if (_sort && !_sorted)
            SortInPlace();

        if (_blob.Length != _blobLength)
            Array.Resize(ref _blob, _blobLength);
        if (_offsets.Length != _count + 1)
            Array.Resize(ref _offsets, _count + 1);
        if (_values.Length != _count)
            Array.Resize(ref _values, _count);

        values = _values;
        return new Utf8WordTable(_blob, _offsets, _count, _buildPrefix);
    }

    private ReadOnlySpan<byte> GetWord(int index) =>
        _blob.AsSpan(_offsets[index], _offsets[index + 1] - _offsets[index]);

    private void EnsureWordSlots()
    {
        if (_count + 2 <= _offsets.Length)
            return;

        var capacity = Math.Max(_offsets.Length * 2, 8);
        Array.Resize(ref _offsets, capacity);
        Array.Resize(ref _values, capacity - 1);
    }

    private void EnsureBlob(int additional)
    {
        var required = _blobLength + additional;
        if (required <= _blob.Length)
            return;

        Array.Resize(ref _blob, Math.Max(_blob.Length * 2, required));
    }

    /// <summary>
    /// Пересобирает таблицу в отсортированном порядке через массив индексов и заодно
    /// склеивает дубликаты (побеждает последняя запись).
    /// </summary>
    private void SortInPlace()
    {
        var order = new int[_count];
        for (var i = 0; i < order.Length; i++)
            order[i] = i;

        Array.Sort(order, (left, right) =>
        {
            var comparison = GetWord(left).SequenceCompareTo(GetWord(right));
            return comparison != 0 ? comparison : left.CompareTo(right);
        });

        var blob = new byte[_blobLength];
        var offsets = new int[_count + 1];
        var values = new int[_count];
        var position = 0;
        var unique = 0;

        foreach (var index in order)
        {
            var word = GetWord(index);
            if (unique > 0 && blob.AsSpan(offsets[unique - 1], offsets[unique] - offsets[unique - 1]).SequenceEqual(word))
            {
                values[unique - 1] = _values[index];
                continue;
            }

            word.CopyTo(blob.AsSpan(position));
            position += word.Length;
            offsets[unique + 1] = position;
            values[unique] = _values[index];
            unique++;
        }

        _blob = blob;
        _offsets = offsets;
        _values = values;
        _blobLength = position;
        _count = unique;
        _sorted = true;
    }
}
