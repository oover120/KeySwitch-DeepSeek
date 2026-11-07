using System.Diagnostics;
using System.Runtime.InteropServices;
using KeySwitch.Core.Text;

namespace KeySwitch.BuildAssets;

/// <summary>
/// Один проход по корпусу Tatoeba: частоты словоформ (для отбора словаря) и счётчики
/// 3/4-грамм (для языковых моделей). Совмещение проходов вдвое сокращает время сборки
/// и гарантирует, что словарь и n-граммы видят один и тот же текст.
/// </summary>
internal sealed class CorpusAnalysis
{
    private CorpusAnalysis(
        Dictionary<string, int> wordCounts,
        Dictionary<ulong, int> counts3,
        Dictionary<ulong, int> counts4,
        long total3,
        long total4,
        long lines,
        long tokens)
    {
        WordCounts = wordCounts;
        Counts3 = counts3;
        Counts4 = counts4;
        Total3 = total3;
        Total4 = total4;
        Lines = lines;
        Tokens = tokens;
    }

    /// <summary>Частота словоформы в корпусе (в нижнем регистре).</summary>
    public Dictionary<string, int> WordCounts { get; }

    public Dictionary<ulong, int> Counts3 { get; }

    public Dictionary<ulong, int> Counts4 { get; }

    /// <summary>Суммарное число учтённых 3-грамм (знаменатель вероятности).</summary>
    public long Total3 { get; }

    public long Total4 { get; }

    public long Lines { get; }

    public long Tokens { get; }

    public static CorpusAnalysis Analyze(Language language, string sentencesPath, Action<string> log)
    {
        var wordCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var counts3 = new Dictionary<ulong, int>(capacity: 1 << 20);
        var counts4 = new Dictionary<ulong, int>(capacity: 1 << 21);
        long total3 = 0;
        long total4 = 0;
        long lines = 0;
        long tokens = 0;

        var stopwatch = Stopwatch.StartNew();
        var words = new List<string>(capacity: 64);

        foreach (var text in CorpusText.ReadTexts(sentencesPath))
        {
            lines++;
            CorpusText.Tokenize(text, language, words);
            foreach (var word in words)
            {
                tokens++;
                CollectionsMarshal.GetValueRefOrAddDefault(wordCounts, word, out _)++;

                var padded = NgramHasher.Pad(word);
                CountGrams(padded, 3, counts3, ref total3);
                CountGrams(padded, 4, counts4, ref total4);
            }

            if (lines % 1_000_000 == 0)
            {
                log($"  {language.Code()}: {lines:N0} строк, {tokens:N0} слов, уникальных {wordCounts.Count:N0}, "
                    + $"3-грамм {counts3.Count:N0}, 4-грамм {counts4.Count:N0} ({stopwatch.Elapsed.TotalSeconds:0} с)");
            }
        }

        log($"{language.Code()}: корпус прочитан за {stopwatch.Elapsed.TotalSeconds:0} с: строк {lines:N0}, слов {tokens:N0}, "
            + $"уникальных слов {wordCounts.Count:N0}.");
        return new CorpusAnalysis(wordCounts, counts3, counts4, total3, total4, lines, tokens);
    }

    /// <summary>Считает n-граммы слова; total — число всех учтённых грамм (для вероятностей).</summary>
    private static void CountGrams(string padded, int order, Dictionary<ulong, int> counts, ref long total)
    {
        if (padded.Length < order)
            return;

        Span<char> buffer = stackalloc char[4];
        for (var i = 0; i <= padded.Length - order; i++)
        {
            padded.AsSpan(i, order).CopyTo(buffer);
            var hash = NgramHasher.Hash(buffer[..order]);
            ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(counts, hash, out _);
            slot++;
            total++;
        }
    }
}
