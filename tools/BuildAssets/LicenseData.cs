using System.Text;

namespace KeySwitch.BuildAssets;

/// <summary>
/// Лицензии и атрибуция источников. Тексты лицензий не переписываются вручную: они копируются
/// из тех же кэшированных файлов, что скачивал <see cref="SourceCache"/>, и дополняются шапкой
/// с URL, датой и отметкой о модификации (этого явно требует лицензия ru_RU).
/// </summary>
internal static class LicenseData
{
    /// <summary>Дата обращения к источникам (фиксированная — чтобы повторные запуски были идемпотентны).</summary>
    public const string SourceDate = "2026-11-06";

    private const string EnDictionaryUrl = "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/en/en_US.dic";
    private const string EnAffixUrl = "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/en/en_US.aff";
    private const string RuDictionaryUrl = "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/ru_RU/ru_RU.dic";
    private const string RuAffixUrl = "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/ru_RU/ru_RU.aff";
    private const string EnFrequencyUrl = "https://raw.githubusercontent.com/hermitdave/FrequencyWords/master/content/2018/en/en_50k.txt";
    private const string RuFrequencyUrl = "https://raw.githubusercontent.com/hermitdave/FrequencyWords/master/content/2018/ru/ru_50k.txt";
    private const string EnSentencesUrl = "https://downloads.tatoeba.org/exports/per_language/eng/eng_sentences.tsv.bz2";
    private const string RuSentencesUrl = "https://downloads.tatoeba.org/exports/per_language/rus/rus_sentences.tsv.bz2";

    public static void Write(string outputDirectory, string cacheDirectory, Action<string> log)
    {
        var licenses = Path.Combine(outputDirectory, "licenses");
        Directory.CreateDirectory(licenses);

        CopyWithHeader(
            Path.Combine(cacheDirectory, "README_en_US.txt"),
            Path.Combine(licenses, "en_US.txt"),
            """
            en_US Hunspell dictionary (LibreOffice dictionaries repository)
            Source: LibreOffice/dictionaries/en (en_US.dic, en_US.aff), https://github.com/LibreOffice/dictionaries
            Copyright: en_US word list — SCOWL, Copyright 2000-2018 Kevin Atkinson; affix file — Ispell (BSD, Geoff Kuenning).
            Modified: yes. KeySwitch.BuildAssets expands affix rules, lowercases, filters by alphabet and length,
            merges the forms with FrequencyWords frequencies and selects at most 200 000 entries. This derivative
            keeps the original notices below and is distributed under the same permissive terms.
            """,
            log);

        CopyWithHeader(
            Path.Combine(cacheDirectory, "README_ru_RU.txt"),
            Path.Combine(licenses, "ru_RU.txt"),
            """
            ru_RU Hunspell dictionary (LibreOffice dictionaries repository)
            Source: LibreOffice/dictionaries/ru_RU (ru_RU.dic, ru_RU.aff), https://github.com/LibreOffice/dictionaries
            Copyright: Alexander I. Lebedev, 1997-2008 (BSD-like license, see below).

            Modified: yes — this is a MODIFIED VERSION of the original word list, as required by the license.
            KeySwitch.BuildAssets expands affix rules, lowercases, filters by the Cyrillic alphabet and length,
            merges the forms with FrequencyWords frequencies and keeps at most 200 000 entries; it also derives
            the yo-ru.txt.gz pair list. Original copyright and license text is preserved below.
            """,
            log);

        CopyWithHeader(
            Path.Combine(cacheDirectory, "LICENSE_FrequencyWords"),
            Path.Combine(licenses, "FrequencyWords.txt"),
            """
            FrequencyWords word frequency lists
            Source: https://github.com/hermitdave/FrequencyWords (content/2018/en/en_50k.txt, content/2018/ru/ru_50k.txt)
            License: MIT (Copyright (c) 2016 Hermit Dave). The lists are used unmodified as frequency input;
            only the numeric frequencies are propagated into words-*.txt.gz.
            """,
            log);

        BuildOptions.WriteText(Path.Combine(licenses, "Tatoeba.txt"), TatoebaNotice());
        BuildOptions.WriteText(Path.Combine(licenses, "SOURCES.md"), SourcesDocument());

        log("licenses: en_US.txt, ru_RU.txt, FrequencyWords.txt, Tatoeba.txt, SOURCES.md.");
    }

    private static void CopyWithHeader(string sourcePath, string targetPath, string header, Action<string> log)
    {
        if (!File.Exists(sourcePath))
        {
            log($"ВНИМАНИЕ: не найден файл лицензии {sourcePath}; записываю только шапку.");
            BuildOptions.WriteText(targetPath, header + Environment.NewLine);
            return;
        }

        var original = File.ReadAllText(sourcePath);
        var builder = new StringBuilder();
        builder.AppendLine(header.ReplaceLineEndings("\n").TrimEnd());
        builder.AppendLine();
        builder.AppendLine(new string('-', 78));
        builder.AppendLine();
        builder.Append(original.ReplaceLineEndings("\n"));
        BuildOptions.WriteText(targetPath, builder.ToString());
    }

    private static string TatoebaNotice()
    {
        return $"""
            Tatoeba sentence corpora
            Sources:
              {EnSentencesUrl}
              {RuSentencesUrl}
            Site: https://tatoeba.org
            License: Creative Commons Attribution 2.0 France (CC BY 2.0 FR),
              https://creativecommons.org/licenses/by/2.0/fr/
            Copyright: the sentences are contributed by Tatoeba community members; each sentence keeps its
            author attribution in the Tatoeba database (https://tatoeba.org). Accessed on {SourceDate}.

            Modified: yes. KeySwitch.BuildAssets tokenizes the sentences and derives aggregate character
            n-gram counts (ngrams-en-3.bin, ngrams-en-4.bin, ngrams-ru-3.bin, ngrams-ru-4.bin). No sentences
            are redistributed. Attribution is provided here and in docs/notes/data.md.
            """;
    }

    private static string SourcesDocument()
    {
        return $"""
            # Источники ассетов KeySwitch

            Все данные скачаны {SourceDate} из открытых источников. Скачивание выполняет только
            `tools/BuildAssets` (шаг сборки), в рантайме сетевой активности нет. Кэш исходников —
            `tools/BuildAssets/obj/cache`, в git не входит. Повторный запуск переиспользует кэш.

            | Файл ассета | Источник | Дата | Назначение | Лицензия |
            |---|---|---|---|---|
            | `words-en.txt.gz` | [{EnDictionaryUrl}]({EnDictionaryUrl}), [{EnAffixUrl}]({EnAffixUrl}), [{EnFrequencyUrl}]({EnFrequencyUrl}) | {SourceDate} | словоформы en_US + частоты, ≤200 000 слов | SCOWL/Ispell (permissive), MIT для FrequencyWords |
            | `words-ru.txt.gz` | [{RuDictionaryUrl}]({RuDictionaryUrl}), [{RuAffixUrl}]({RuAffixUrl}), [{RuFrequencyUrl}]({RuFrequencyUrl}) | {SourceDate} | словоформы ru_RU + частоты, ≤200 000 слов | BSD-подобная (A. I. Lebedev), MIT для FrequencyWords |
            | `ngrams-en-3.bin`, `ngrams-en-4.bin` | [{EnSentencesUrl}]({EnSentencesUrl}) | {SourceDate} | символьные 3/4-граммы из английских предложений Tatoeba | CC BY 2.0 FR |
            | `ngrams-ru-3.bin`, `ngrams-ru-4.bin` | [{RuSentencesUrl}]({RuSentencesUrl}) | {SourceDate} | символьные 3/4-граммы из русских предложений Tatoeba | CC BY 2.0 FR |
            | `yo-ru.txt.gz` | производный от `ru_RU.dic` | {SourceDate} | пары «е → ё» для постобработки | BSD-подобная (A. I. Lebedev) |
            | `typos-ru.txt`, `typos-en.txt` | курируется в `tools/BuildAssets/TyposData.cs` | {SourceDate} | частые опечатки | MIT (код проекта) |
            | `licenses/*` | тексты лицензий перечисленных источников | {SourceDate} | соблюдение условий распространения | — |

            Русский словарь распространяется как **модифицированная версия**: правила аффиксов
            развёрнуты, регистр приведён к нижнему, список отфильтрован и ограничен по частоте.
            Это прямо разрешено лицензией при условии явной отметки о модификации — она есть
            в `licenses/ru_RU.txt` и в этом файле.
            """;
    }
}
