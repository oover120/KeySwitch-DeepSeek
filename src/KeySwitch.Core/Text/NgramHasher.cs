using System.Text;

namespace KeySwitch.Core.Text;

/// <summary>
/// Хеширование n-грамм. Один и тот же алгоритм используется инструментом сборки данных
/// (tools/BuildAssets) и загрузчиком моделей, поэтому формат файлов не зависит от строкового
/// представления и не требует хранения самих n-грамм.
/// </summary>
public static class NgramHasher
{
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>FNV-1a по UTF-16 code units (стабильно между платформами и версиями .NET).</summary>
    public static ulong Hash(ReadOnlySpan<char> gram)
    {
        var hash = FnvOffset;
        foreach (var ch in gram)
        {
            hash = (hash ^ (byte)(ch & 0xFF)) * FnvPrime;
            hash = (hash ^ (byte)(ch >> 8)) * FnvPrime;
        }
        return hash;
    }

    public static ulong Hash(string gram) => Hash(gram.AsSpan());

    /// <summary>Строка для обучения: границы слова помечаются символами ^ и $.</summary>
    public static string Pad(string word) => "^" + WordText.Normalize(word) + "$";

    public static int CountGrams(string word, int order)
    {
        var padded = Pad(word);
        return padded.Length >= order ? padded.Length - order + 1 : 0;
    }

    /// <summary>Перечисляет все n-граммы указанного порядка без выделения памяти под каждую строку.</summary>
    public static void ForEachGram(string word, int order, Action<ulong> action)
    {
        var padded = Pad(word);
        if (padded.Length < order)
            return;

        Span<char> buffer = stackalloc char[order];
        for (var i = 0; i <= padded.Length - order; i++)
        {
            padded.AsSpan(i, order).CopyTo(buffer);
            action(Hash(buffer));
        }
    }

    public static IEnumerable<string> EnumerateGrams(string word, int order)
    {
        var padded = Pad(word);
        if (padded.Length < order)
            yield break;

        var sb = new StringBuilder(order);
        for (var i = 0; i <= padded.Length - order; i++)
        {
            sb.Clear();
            sb.Append(padded, i, order);
            yield return sb.ToString();
        }
    }
}
