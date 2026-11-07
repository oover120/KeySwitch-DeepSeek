# Источники ассетов KeySwitch

Все данные скачаны 2026-11-06 из открытых источников. Скачивание выполняет только
`tools/BuildAssets` (шаг сборки), в рантайме сетевой активности нет. Кэш исходников —
`tools/BuildAssets/obj/cache`, в git не входит. Повторный запуск переиспользует кэш.

| Файл ассета | Источник | Дата | Назначение | Лицензия |
|---|---|---|---|---|
| `words-en.txt.gz` | [https://raw.githubusercontent.com/LibreOffice/dictionaries/master/en/en_US.dic](https://raw.githubusercontent.com/LibreOffice/dictionaries/master/en/en_US.dic), [https://raw.githubusercontent.com/LibreOffice/dictionaries/master/en/en_US.aff](https://raw.githubusercontent.com/LibreOffice/dictionaries/master/en/en_US.aff), [https://raw.githubusercontent.com/hermitdave/FrequencyWords/master/content/2018/en/en_50k.txt](https://raw.githubusercontent.com/hermitdave/FrequencyWords/master/content/2018/en/en_50k.txt) | 2026-11-06 | словоформы en_US + частоты, ≤200 000 слов | SCOWL/Ispell (permissive), MIT для FrequencyWords |
| `words-ru.txt.gz` | [https://raw.githubusercontent.com/LibreOffice/dictionaries/master/ru_RU/ru_RU.dic](https://raw.githubusercontent.com/LibreOffice/dictionaries/master/ru_RU/ru_RU.dic), [https://raw.githubusercontent.com/LibreOffice/dictionaries/master/ru_RU/ru_RU.aff](https://raw.githubusercontent.com/LibreOffice/dictionaries/master/ru_RU/ru_RU.aff), [https://raw.githubusercontent.com/hermitdave/FrequencyWords/master/content/2018/ru/ru_50k.txt](https://raw.githubusercontent.com/hermitdave/FrequencyWords/master/content/2018/ru/ru_50k.txt) | 2026-11-06 | словоформы ru_RU + частоты, ≤200 000 слов | BSD-подобная (A. I. Lebedev), MIT для FrequencyWords |
| `ngrams-en-3.bin`, `ngrams-en-4.bin` | [https://downloads.tatoeba.org/exports/per_language/eng/eng_sentences.tsv.bz2](https://downloads.tatoeba.org/exports/per_language/eng/eng_sentences.tsv.bz2) | 2026-11-06 | символьные 3/4-граммы из английских предложений Tatoeba | CC BY 2.0 FR |
| `ngrams-ru-3.bin`, `ngrams-ru-4.bin` | [https://downloads.tatoeba.org/exports/per_language/rus/rus_sentences.tsv.bz2](https://downloads.tatoeba.org/exports/per_language/rus/rus_sentences.tsv.bz2) | 2026-11-06 | символьные 3/4-граммы из русских предложений Tatoeba | CC BY 2.0 FR |
| `yo-ru.txt.gz` | производный от `ru_RU.dic` | 2026-11-06 | пары «е → ё» для постобработки | BSD-подобная (A. I. Lebedev) |
| `typos-ru.txt`, `typos-en.txt` | курируется в `tools/BuildAssets/TyposData.cs` | 2026-11-06 | частые опечатки | MIT (код проекта) |
| `licenses/*` | тексты лицензий перечисленных источников | 2026-11-06 | соблюдение условий распространения | — |

Русский словарь распространяется как **модифицированная версия**: правила аффиксов
развёрнуты, регистр приведён к нижнему, список отфильтрован и ограничен по частоте.
Это прямо разрешено лицензией при условии явной отметки о модификации — она есть
в `licenses/ru_RU.txt` и в этом файле.