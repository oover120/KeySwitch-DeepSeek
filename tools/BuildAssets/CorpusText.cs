using System.Text;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;

namespace KeySwitch.BuildAssets;

/// <summary>
/// Чтение и токенизация корпусов Tatoeba. Один и тот же токенизатор используется и для
/// частот слов, и для n-грамм, чтобы представление слов совпадало с Detect\LanguageScorer
/// (слово = последовательность букв целевого алфавита в нижнем регистре).
/// </summary>
internal static class CorpusText
{
    /// <summary>Читает bz2-выгрузку Tatoeba и отдаёт текст каждой строки (последняя колонка после таба).</summary>
    public static IEnumerable<string> ReadTexts(string sentencesPath)
    {
        using var file = File.OpenRead(sentencesPath);
        using var bzip = BZip2Stream.Create(file, CompressionMode.Decompress, decompressConcatenated: false, leaveOpen: false);
        using var reader = new StreamReader(bzip, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 20);

        while (reader.ReadLine() is { } line)
        {
            var tab = line.LastIndexOf('\t');
            var text = tab >= 0 ? line[(tab + 1)..] : line;
            if (text.Length > 0)
                yield return text;
        }
    }

    /// <summary>Режет текст на слова целевого алфавита в нижнем регистре.</summary>
    public static void Tokenize(string text, Language language, List<string> words)
    {
        words.Clear();
        var builder = new StringBuilder(32);

        foreach (var ch in text)
        {
            var lower = char.ToLowerInvariant(ch);
            var isLetter = language == Language.Russian ? Languages.IsCyrillic(lower) : Languages.IsLatin(lower);
            if (isLetter)
            {
                builder.Append(lower);
                continue;
            }

            if (builder.Length > 0)
            {
                words.Add(builder.ToString());
                builder.Clear();
            }
        }

        if (builder.Length > 0)
            words.Add(builder.ToString());
    }
}
