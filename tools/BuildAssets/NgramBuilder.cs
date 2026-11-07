using KeySwitch.Core.Text;

namespace KeySwitch.BuildAssets;

/// <summary>
/// Запись символьных 3/4-граммных моделей из посчитанного корпуса. Хеши и log10-вероятности
/// пишутся через <see cref="NgramModelFile.Write"/>, поэтому формат не может разъехаться с ядром.
/// </summary>
internal static class NgramBuilder
{
    /// <summary>
    /// Минимальное число встреч n-граммы. Раньше было 3, но при небольшом корпусе это оставляло
    /// в модели только очень частые сочетания (ru-3 — ~10 тыс. записей) и почти все слова
    /// получали floor −6.0. Теперь сохраняются все встреченные n-граммы (порог 1).
    /// </summary>
    public const int MinimumCount = 1;

    /// <summary>Нижняя граница log10(p) в файле: отсутствующие граммы ядро трактует как −6.0.</summary>
    public const double MinLogProbability = -6.0;

    public static void WriteModels(
        Language language,
        CorpusAnalysis analysis,
        string outputDirectory,
        int maxGrams,
        Action<string> log)
    {
        var written3 = WriteModel(
            Path.Combine(outputDirectory, $"ngrams-{language.Code()}-3.bin"), 3, analysis.Counts3, analysis.Total3, maxGrams, log);
        var written4 = WriteModel(
            Path.Combine(outputDirectory, $"ngrams-{language.Code()}-4.bin"), 4, analysis.Counts4, analysis.Total4, maxGrams, log);
        log($"{language.Code()}: записано 3-грамм {written3:N0}, 4-грамм {written4:N0}.");
    }

    private static int WriteModel(
        string path,
        int order,
        Dictionary<ulong, int> counts,
        long total,
        int maxGrams,
        Action<string> log)
    {
        var selected = new List<KeyValuePair<ulong, int>>(Math.Min(counts.Count, maxGrams * 2));
        foreach (var pair in counts)
        {
            if (pair.Value >= MinimumCount)
                selected.Add(pair);
        }

        selected.Sort(static (a, b) =>
        {
            var byCount = b.Value.CompareTo(a.Value);
            return byCount != 0 ? byCount : a.Key.CompareTo(b.Key);
        });

        if (selected.Count > maxGrams)
        {
            log($"  {order}-граммы: отсечение {selected.Count:N0} → {maxGrams:N0} по частоте.");
            selected.RemoveRange(maxGrams, selected.Count - maxGrams);
        }

        var entries = new List<KeyValuePair<ulong, float>>(selected.Count);
        foreach (var pair in selected)
        {
            var probability = total > 0 ? (double)pair.Value / total : 0;
            // Редкая, но встреченная n-грамма не должна быть хуже отсутствующей (floor −6.0),
            // иначе модель штрафует реальные слова сильнее, чем невозможные сочетания.
            var log10 = probability > 0 ? Math.Max(Math.Log10(probability), MinLogProbability) : MinLogProbability;
            entries.Add(new KeyValuePair<ulong, float>(pair.Key, (float)log10));
        }

        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            NgramModelFile.Write(stream, order, entries);

        File.Move(temporary, path, overwrite: true);
        var size = new FileInfo(path).Length;
        log($"  {Path.GetFileName(path)}: {entries.Count:N0} записей, {SourceCache.FormatSize(size)}, всего грамм {total:N0}.");
        return entries.Count;
    }
}
