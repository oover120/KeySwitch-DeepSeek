using System.Collections.Generic;

namespace KeySwitch.App.Localization;

/// <summary>
/// Строки окон настроек: заголовок, вкладки, подписи полей, подсказки, сообщения валидации.
/// Ключи пишутся в нижнем регистре через точку: "general.title", "exceptions.add".
/// Каждый ключ обязан существовать и в Ru, и в En.
/// </summary>
internal static class UiStrings
{
    internal static readonly Dictionary<string, string> Ru = new(System.StringComparer.Ordinal)
    {
        ["app.name"] = "KeySwitch DeepSeek",
        ["app.tagline"] = "Автоматический переключатель раскладки RU/EN",
        ["app.badge"] = "KS",
        ["action.add"] = "Добавить",
        ["action.remove"] = "Удалить",

        // Окно настроек.
        ["settings.title"] = "настройки",
        ["settings.close"] = "Закрыть",
        ["settings.cancel"] = "Отмена",
        ["settings.save"] = "Сохранить",
        ["settings.tab.general"] = "Общие",
        ["settings.tab.exceptions"] = "Исключения",
        ["settings.tab.typos"] = "Опечатки",
        ["settings.tab.sounds"] = "Звуки",
        ["settings.tab.about"] = "О программе",

        // Вкладка «Общие».
        ["general.section.behavior"] = "Поведение",
        ["general.enabled.header"] = "Приложение включено",
        ["general.enabled.description"] =
            "Автопереключение, значок в трее и горячие клавиши работают, пока переключатель включён.",
        ["general.autoswitch.header"] = "Автоматически исправлять раскладку",
        ["general.autoswitch.description"] =
            "Следить за последним словом и менять раскладку, когда оно набрано не в той.",
        ["general.section.early"] = "Раннее срабатывание",
        ["general.early.header"] = "Исправлять до пробела",
        ["general.early.description"] =
            "Переключать раскладку сразу, как только слово стало достаточно однозначным, не дожидаясь пробела.",
        ["general.earlyLength.header"] = "Минимальная длина слова",
        ["general.earlyLength.description"] =
            "Слова короче не трогаем: короткие слова слишком легко испортить.",
        ["general.earlyLength.unit"] = "символов",
        ["general.section.keys"] = "Клавиши и жесты",
        ["general.singleShift.header"] = "Одиночный Shift переключает раскладку",
        ["general.singleShift.description"] =
            "Одиночное нажатие Shift без других клавиш меняет язык ввода.",
        ["general.doubleShift.header"] = "Двойной Shift отменяет или переключает слово",
        ["general.doubleShift.description"] =
            "Быстрое двойное нажатие Shift: отменяет последнее исправление, а если его не было — принудительно переводит последнее слово в другую раскладку, даже когда автопереключение отказалось.",
        ["general.hotkey.header"] = "Ручная конвертация выделенного",
        ["general.hotkey.description"] =
            "Сочетание для принудительной смены раскладки. Нажмите на поле и введите сочетание; пусто — выключено.",
        ["general.pause.header"] = "Pause/Break — пауза",
        ["general.pause.description"] =
            "Фиксированная клавиша: временно приостановить автопереключение.",
        ["general.section.games"] = "Игры",
        ["general.games.header"] = "Не исправлять раскладку в играх",
        ["general.games.description"] =
            "Автопереключение отключается в полноэкранных играх, в программах из папок Games и " +
            "Steam/Epic/GOG, а также в окнах игровых движков (Unity, Unreal, SDL, GLFW). " +
            "В обычных приложениях, включая браузеры, всё работает как обычно.",
        ["general.section.accuracy"] = "Точность",
        ["general.confidence.header"] = "Порог уверенности",
        ["general.confidence.description"] =
            "Насколько уверенно движок должен распознать опечатку. Меньше — срабатывает чаще, больше — осторожнее.",
        ["general.section.startup"] = "Запуск и безопасность",
        ["general.startup.header"] = "Запускать при входе в Windows",
        ["general.startup.description"] =
            "Приложение будет стартовать вместе с системой и жить в трее.",
        ["general.admin.header"] = "Запускать от администратора",
        ["general.admin.description"] =
            "Нужно, чтобы хук работал в приложениях, запущенных с повышенными правами.",
        ["general.safe.header"] = "Безопасный режим",
        ["general.safe.description"] =
            "Без хука клавиатуры и обучения — только ручная конвертация и настройки. Полезно, если что-то мешает работе системы.",
        ["general.section.appearance"] = "Оформление",
        ["general.theme.header"] = "Тема",
        ["general.theme.description"] = "Системная тема следует за оформлением Windows.",
        ["general.language.header"] = "Язык интерфейса",
        ["general.language.description"] = "Язык применяется сразу, без перезапуска.",

        // Вкладка «Исключения».
        ["exceptions.section.processes"] = "Программы",
        ["exceptions.processes.hint"] =
            "В приложениях из списка автопереключение ведёт себя по выбранному режиму: полная обработка, " +
            "только ручная конвертация или полное отключение. Игры и папка Games отключаются глобально.",
        ["exceptions.process.tooltip"] = "Имя процесса или путь к exe: Code.exe",
        ["exceptions.add"] = "Добавить",
        ["exceptions.browse"] = "Выбрать программу…",
        ["exceptions.remove"] = "Удалить",
        ["exceptions.processes.empty"] = "Список пуст: автопереключение работает во всех программах.",
        ["exceptions.words.empty"] = "Слов пока нет: добавьте слово, которое нужно оставить как есть.",
        ["exceptions.section.words"] = "Слова-исключения",
        ["exceptions.words.hint"] =
            "Эти слова никогда не переключаются автоматически — ни при наборе, ни при исправлении. " +
            "Направление можно ограничить.",
        ["exceptions.word.tooltip"] = "Слово, которое нужно оставить как есть",
        ["exceptions.word.fromLearning"] = "из обучения",

        // Вкладка «Опечатки».
        ["typos.section.correction"] = "Исправление",
        ["typos.enabled.header"] = "Исправлять опечатки",
        ["typos.enabled.description"] =
            "Подменять частые опечатки по словарю: «привет» с лишней буквой, «сейчас», «только» и т. п.",
        ["typos.caps.header"] = "Исправлять регистр",
        ["typos.caps.description"] =
            "ИСправление → Исправление, ПРивет → Привет: если слово набрано с зажатым Caps Lock.",
        ["typos.yo.header"] = "Ёфикатор",
        ["typos.yo.description"] = "Расставлять «ё» в словах, где она пишется: ещё, её, зовём.",
        ["typos.dictionary"] = "Словарь замен",
        ["typos.hint"] =
            "Замены применяются к целым словам. Неверное слово записывается строчными буквами, " +
            "верное — как нужно в тексте.",
        ["typos.wrong.tooltip"] = "Как встречается: првиет",
        ["typos.right.tooltip"] = "Как должно быть: привет",
        ["typos.empty"] = "Словарь пуст: добавьте первую замену.",
        ["typos.files"] = "Файлы словаря: {0} · {1}",

        // Вкладка «Звуки».
        ["sounds.section"] = "Звуковое оповещение",
        ["sounds.enabled.header"] = "Включить звуки",
        ["sounds.enabled.description"] =
            "Короткий сигнал, когда раскладка переключена или исправлена опечатка.",
        ["sounds.switch.header"] = "Звук переключения раскладки",
        ["sounds.switch.description"] = "WAV-файл, который играет при смене языка.",
        ["sounds.fix.header"] = "Звук исправления",
        ["sounds.fix.description"] = "WAV-файл для случаев, когда исправлена опечатка или регистр.",
        ["sounds.browse"] = "Выбрать…",
        ["sounds.preview"] = "Прослушать",
        ["sounds.volume.header"] = "Громкость",
        ["sounds.volume.description"] =
            "Общая громкость звуков приложения, не затрагивает системную.",
        ["sounds.hint"] =
            "Поддерживаются файлы WAV. Если файл не выбран, звук переключения не играет. " +
            "Кнопка «Прослушать» работает из запущенного приложения; выбранный путь сохранится и после перезапуска.",

        // Вкладка «О программе».
        ["about.version"] = "Версия {0} · автопереключатель раскладки RU ↔ EN",
        ["about.intro"] =
            "Переключает раскладку автоматически, когда слово набрано не в том языке. Работает локально: " +
            "ввод не покидает компьютер, сеть не используется.",
        ["about.learningSummary"] =
            "Отмен: {0} · принято исправлений: {1} · слов с подавлением: {2}",
        ["about.section.limitations"] = "Ограничения",
        ["about.limitations.rdp"] =
            "• Удалённый рабочий стол (RDP) и виртуальные машины: низкоуровневый хук может не видеть ввод. " +
            "В этих сессиях автопереключение может не срабатывать.",
        ["about.limitations.layouts"] =
            "• Умляуты и нестандартные раскладки: поддерживается пара US ↔ ЙЦУКЕН. Другие пары, " +
            "включая немецкую раскладку с умляутами, не гарантируются.",
        ["about.limitations.code"] =
            "• Аббревиатуры и код: слова из 1–3 символов, идентификаторы и имена переменных не трогаются, " +
            "чтобы не портить код и сокращения.",
        ["about.limitations.rare"] =
            "• Искусственные языки и редкие слова: решение принимается по словарю и n-граммам, " +
            "поэтому незнакомое слово может остаться без исправления.",
        ["about.limitations.games"] =
            "• Игры и приложения с прямым вводом (DirectInput): в них автопереключение не работает. " +
            "Добавляйте такие программы в исключения.",
        ["about.section.data"] = "Данные и лицензии",
        ["about.dataFolder"] = "Папка данных",
        ["about.openDataFolder"] = "Открыть папку данных",
        ["about.openReadme"] = "Открыть README",
        ["about.checkUpdates"] = "Проверить обновления",
        ["update.title"] = "Обновления",
        ["update.upToDate"] = "Установлена последняя версия: {0}.",
        ["update.available"] = "Доступна версия {0} (у вас {1}).\n\nОткрыть страницу загрузки?",
        ["update.failed"] = "Не удалось проверить обновления.\n\n{0}",
        ["about.dictionaries"] = "Словари и лицензии",
        ["about.licensesFallback"] =
            "Встроенные словари и таблицы частот собраны из открытых источников.\n" +
            "Полные тексты лицензий поставляются в папке Assets/licenses и появляются на этой вкладке " +
            "после сборки ресурсов.",

        // Выпадающие списки.
        ["options.theme.system"] = "Как в системе",
        ["options.theme.light"] = "Светлая",
        ["options.theme.dark"] = "Тёмная",
        ["options.language.ru"] = "Русский",
        ["options.language.en"] = "English",
        ["options.language.system"] = "Как в системе",
        ["options.typo.ru"] = "Русский",
        ["options.typo.en"] = "Английский",
        ["options.direction.both"] = "В обе стороны",
        ["options.direction.ruEn"] = "RU → EN",
        ["options.direction.enRu"] = "EN → RU",
        ["options.mode.full"] = "Полная обработка",
        ["options.mode.manual"] = "Только вручную",
        ["options.mode.off"] = "Выключено",

        // Поле горячей клавиши.
        ["hotkey.notSet"] = "Не задано",
        ["hotkey.press"] = "Нажмите сочетание…",

        // Диалоги выбора файлов.
        ["dialog.chooseApp"] = "Выберите программу",
        ["dialog.switchSound"] = "Звук переключения раскладки",
        ["dialog.fixSound"] = "Звук исправления",
        ["filter.programs"] = "Программы (*.exe)|*.exe|Все файлы (*.*)|*.*",
        ["filter.sounds"] = "Звуковые файлы (*.wav)|*.wav|Все файлы (*.*)|*.*",

        // Статусы в нижней панели.
        ["status.saved"] = "Настройки сохранены.",
        ["status.soundPathChosen"] = "Путь к звуку выбран. Сохраните настройки.",
        ["status.chooseSoundFirst"] = "Сначала выберите wav-файл.",
        ["status.soundNotFound"] = "Файл не найден: {0}",
        ["status.previewUnavailable"] =
            "Прослушивание доступно из запущенного приложения (звук будет применён после сохранения).",
        ["status.playing"] = "Воспроизведение…",
        ["status.dataFolderFailed"] = "Не удалось открыть папку данных: {0}",
        ["status.readmeMissing"] = "README.md не найден рядом с приложением.",
        ["status.readmeFailed"] = "Не удалось открыть README: {0}",

        // Ошибки валидации и ввода.
        ["error.saveData"] = "Не удалось записать файлы данных: {0}",
        ["error.typosSave"] = "Не удалось сохранить словарь опечаток: {0}",
        ["error.earlyLength"] = "Минимальная длина для раннего срабатывания — от 2 до 20 символов.",
        ["error.confidence"] = "Порог уверенности должен быть в диапазоне 0.5–5.0.",
        ["error.hotkey"] =
            "Сочетание клавиш должно заканчиваться клавишей, например Ctrl+Shift+Space.",
        ["error.typoReplacement"] =
            "Проверьте замену для «{0}»: она не должна быть пустой и не должна содержать «=».",
        ["error.processEmpty"] = "Укажите имя процесса, например Code.exe.",
        ["error.processDuplicate"] = "Такой процесс уже есть в списке.",
        ["error.wordEmpty"] = "Слово не задано.",
        ["error.wordDigits"] = "Числа и слова с цифрами не добавляются в исключения.",
        ["error.wordLetters"] = "Слово должно состоять только из букв.",
        ["error.wordAlphabet"] = "Поддерживаются только русские и английские буквы.",
        ["error.wordTooShort"] = "Слишком короткое слово: нужно минимум 2 буквы.",
        ["error.wordMixed"] = "Слово не должно смешивать русские и английские буквы.",
        ["error.wordFrequent"] = "Это слово входит в топ-100 частотных — его нельзя исключать.",
        ["error.wordGeneric"] = "Это слово нельзя добавить в исключения.",
        ["error.wordDuplicateDirection"] = "Такое слово уже есть в этом направлении.",
        ["error.wordConflict"] =
            "Это слово уже исключено в одном из направлений — уберите старую запись.",
        ["error.typoBothFields"] = "Заполните оба поля: что искать и на что заменять.",
        ["error.typoEquals"] = "Символ «=» разделяет пары в файле словаря — его использовать нельзя.",
        ["error.typoSame"] = "Замена совпадает с исходным словом.",
    };

    internal static readonly Dictionary<string, string> En = new(System.StringComparer.Ordinal)
    {
        ["app.name"] = "KeySwitch DeepSeek",
        ["app.tagline"] = "Automatic RU/EN keyboard layout switcher",
        ["app.badge"] = "KS",
        ["action.add"] = "Add",
        ["action.remove"] = "Remove",

        // Settings window.
        ["settings.title"] = "settings",
        ["settings.close"] = "Close",
        ["settings.cancel"] = "Cancel",
        ["settings.save"] = "Save",
        ["settings.tab.general"] = "General",
        ["settings.tab.exceptions"] = "Exceptions",
        ["settings.tab.typos"] = "Typos",
        ["settings.tab.sounds"] = "Sounds",
        ["settings.tab.about"] = "About",

        // General tab.
        ["general.section.behavior"] = "Behavior",
        ["general.enabled.header"] = "App is on",
        ["general.enabled.description"] =
            "Autoswitch, the tray icon and hotkeys work while this switch is on.",
        ["general.autoswitch.header"] = "Fix the layout automatically",
        ["general.autoswitch.description"] =
            "Watch the last word and switch the layout when it was typed in the wrong one.",
        ["general.section.early"] = "Early switching",
        ["general.early.header"] = "Fix before Space",
        ["general.early.description"] =
            "Switch as soon as the word becomes unambiguous, without waiting for Space.",
        ["general.earlyLength.header"] = "Minimum word length",
        ["general.earlyLength.description"] =
            "Shorter words are left alone — there is too little evidence to be sure.",
        ["general.earlyLength.unit"] = "characters",
        ["general.section.keys"] = "Keys and gestures",
        ["general.singleShift.header"] = "Single Shift switches the layout",
        ["general.singleShift.description"] =
            "A single Shift press without other keys changes the input language.",
        ["general.doubleShift.header"] = "Double Shift undoes or converts a word",
        ["general.doubleShift.description"] =
            "A quick double Shift press undoes the last correction; if there was none, it force-converts the last word to the other layout even when autoswitch declined.",
        ["general.hotkey.header"] = "Manual conversion of the selection",
        ["general.hotkey.description"] =
            "Shortcut for a forced layout switch. Click the field and press a combination; empty means off.",
        ["general.pause.header"] = "Pause/Break pauses",
        ["general.pause.description"] =
            "A fixed key that temporarily pauses autoswitch.",
        ["general.section.games"] = "Games",
        ["general.games.header"] = "Do not correct layout in games",
        ["general.games.description"] =
            "Autoswitch turns off in fullscreen games, in programs from Games folders and " +
            "Steam/Epic/GOG libraries, and in windows of game engines (Unity, Unreal, SDL, GLFW). " +
            "Regular apps, browsers included, work as usual.",
        ["general.section.accuracy"] = "Accuracy",
        ["general.confidence.header"] = "Confidence threshold",
        ["general.confidence.description"] =
            "How confident the engine must be before switching. Lower switches more often, higher is more cautious.",
        ["general.section.startup"] = "Startup and safety",
        ["general.startup.header"] = "Start with Windows",
        ["general.startup.description"] =
            "The app starts with Windows and lives in the tray.",
        ["general.admin.header"] = "Run as administrator",
        ["general.admin.description"] =
            "Needed so the hook works in elevated apps.",
        ["general.safe.header"] = "Safe mode",
        ["general.safe.description"] =
            "No keyboard hook and no learning — only manual conversion and settings. Useful when something interferes with the system.",
        ["general.section.appearance"] = "Appearance",
        ["general.theme.header"] = "Theme",
        ["general.theme.description"] = "The system theme follows Windows.",
        ["general.language.header"] = "Interface language",
        ["general.language.description"] =
            "The language applies immediately, no restart needed.",

        // Exceptions tab.
        ["exceptions.section.processes"] = "Apps",
        ["exceptions.processes.hint"] =
            "For apps in the list, autoswitch follows the chosen mode: full processing, manual conversion " +
            "only, or off. Games and the Games folder are disabled globally.",
        ["exceptions.process.tooltip"] = "Process name or path to an exe: Code.exe",
        ["exceptions.add"] = "Add",
        ["exceptions.browse"] = "Choose app…",
        ["exceptions.remove"] = "Remove",
        ["exceptions.processes.empty"] = "The list is empty: autoswitch works in every app.",
        ["exceptions.words.empty"] = "No words yet: add a word that should always stay as typed.",
        ["exceptions.section.words"] = "Word exceptions",
        ["exceptions.words.hint"] =
            "These words are never switched automatically, neither while typing nor during correction. " +
            "The direction can be limited.",
        ["exceptions.word.tooltip"] = "Word to leave as is",
        ["exceptions.word.fromLearning"] = "from learning",

        // Typos tab.
        ["typos.section.correction"] = "Correction",
        ["typos.enabled.header"] = "Fix typos",
        ["typos.enabled.description"] =
            "Replace common typos from the dictionary, such as an extra letter in a word.",
        ["typos.caps.header"] = "Fix letter case",
        ["typos.caps.description"] =
            "hELLO → Hello: when the word was typed with Caps Lock on.",
        ["typos.yo.header"] = "Yo fixer",
        ["typos.yo.description"] = "Restore the Russian letter “ё” in words where it belongs.",
        ["typos.dictionary"] = "Replacement dictionary",
        ["typos.hint"] =
            "Replacements apply to whole words. The typo is stored in lowercase, " +
            "the correction as it should appear in text.",
        ["typos.wrong.tooltip"] = "How it appears: e.g. teh",
        ["typos.right.tooltip"] = "What it should be: e.g. the",
        ["typos.empty"] = "The dictionary is empty: add your first replacement.",
        ["typos.files"] = "Dictionary files: {0} · {1}",

        // Sounds tab.
        ["sounds.section"] = "Sound notifications",
        ["sounds.enabled.header"] = "Enable sounds",
        ["sounds.enabled.description"] =
            "A short cue when the layout is switched or a typo is corrected.",
        ["sounds.switch.header"] = "Layout switch sound",
        ["sounds.switch.description"] = "WAV file played when the language changes.",
        ["sounds.fix.header"] = "Correction sound",
        ["sounds.fix.description"] = "WAV file played when a typo or letter case is corrected.",
        ["sounds.browse"] = "Browse…",
        ["sounds.preview"] = "Preview",
        ["sounds.volume.header"] = "Volume",
        ["sounds.volume.description"] =
            "App sound volume; system volume is not affected.",
        ["sounds.hint"] =
            "WAV files are supported. If no file is selected, no sound plays. " +
            "Preview works in the running app; the chosen path is kept after restart.",

        // About tab.
        ["about.version"] = "Version {0} · RU ↔ EN layout switcher",
        ["about.intro"] =
            "Switches the layout automatically when a word was typed in the wrong language. Runs locally: " +
            "your input never leaves the computer, no network is used.",
        ["about.learningSummary"] =
            "Undos: {0} · corrections accepted: {1} · words suppressed: {2}",
        ["about.section.limitations"] = "Limitations",
        ["about.limitations.rdp"] =
            "• Remote Desktop (RDP) and virtual machines: the low-level hook may not see input, " +
            "so autoswitch may not work there.",
        ["about.limitations.layouts"] =
            "• Umlauts and non-standard layouts: only the US ↔ ЙЦУКЕН pair is supported. " +
            "Other pairs, including German layouts with umlauts, aren't guaranteed.",
        ["about.limitations.code"] =
            "• Abbreviations and code: 1–3 character words, identifiers and variable names are left " +
            "alone so code and short forms aren't broken.",
        ["about.limitations.rare"] =
            "• Constructed languages and rare words: decisions rely on the dictionary and n-grams, " +
            "so an unfamiliar word may stay uncorrected.",
        ["about.limitations.games"] =
            "• Games and apps with direct input (DirectInput): autoswitch doesn't work there. " +
            "Add such apps to exceptions.",
        ["about.section.data"] = "Data and licenses",
        ["about.dataFolder"] = "Data folder",
        ["about.openDataFolder"] = "Open data folder",
        ["about.openReadme"] = "Open README",
        ["about.checkUpdates"] = "Check for updates",
        ["update.title"] = "Updates",
        ["update.upToDate"] = "You have the latest version: {0}.",
        ["update.available"] = "Version {0} is available (you have {1}).\n\nOpen the download page?",
        ["update.failed"] = "Could not check for updates.\n\n{0}",
        ["about.dictionaries"] = "Dictionaries and licenses",
        ["about.licensesFallback"] =
            "The built-in dictionaries and frequency tables come from open sources.\n" +
            "Full license texts ship in Assets/licenses and appear here " +
            "after the resources are built.",

        // Drop-down options.
        ["options.theme.system"] = "Use system theme",
        ["options.theme.light"] = "Light",
        ["options.theme.dark"] = "Dark",
        ["options.language.ru"] = "Russian",
        ["options.language.en"] = "English",
        ["options.language.system"] = "Use system language",
        ["options.typo.ru"] = "Russian",
        ["options.typo.en"] = "English",
        ["options.direction.both"] = "Both ways",
        ["options.direction.ruEn"] = "RU → EN",
        ["options.direction.enRu"] = "EN → RU",
        ["options.mode.full"] = "Full processing",
        ["options.mode.manual"] = "Manual only",
        ["options.mode.off"] = "Off",

        // Hotkey field.
        ["hotkey.notSet"] = "Not set",
        ["hotkey.press"] = "Press a combination…",

        // File dialogs.
        ["dialog.chooseApp"] = "Choose an app",
        ["dialog.switchSound"] = "Layout switch sound",
        ["dialog.fixSound"] = "Correction sound",
        ["filter.programs"] = "Applications (*.exe)|*.exe|All files (*.*)|*.*",
        ["filter.sounds"] = "Sound files (*.wav)|*.wav|All files (*.*)|*.*",

        // Status messages.
        ["status.saved"] = "Settings saved.",
        ["status.soundPathChosen"] = "Sound path selected. Save the settings.",
        ["status.chooseSoundFirst"] = "Choose a WAV file first.",
        ["status.soundNotFound"] = "File not found: {0}",
        ["status.previewUnavailable"] =
            "Preview is available in the running app (the sound will apply after saving).",
        ["status.playing"] = "Playing…",
        ["status.dataFolderFailed"] = "Couldn't open the data folder: {0}",
        ["status.readmeMissing"] = "README.md wasn't found next to the app.",
        ["status.readmeFailed"] = "Couldn't open README: {0}",

        // Validation and input errors.
        ["error.saveData"] = "Couldn't write the data files: {0}",
        ["error.typosSave"] = "Couldn't save the typo dictionary: {0}",
        ["error.earlyLength"] = "The minimum early-switch length must be between 2 and 20 characters.",
        ["error.confidence"] = "The confidence threshold must be between 0.5 and 5.0.",
        ["error.hotkey"] =
            "The shortcut must end with a key, for example Ctrl+Shift+Space.",
        ["error.typoReplacement"] =
            "Check the replacement for \"{0}\": it must not be empty and must not contain \"=\".",
        ["error.processEmpty"] = "Enter a process name, for example Code.exe.",
        ["error.processDuplicate"] = "That process is already in the list.",
        ["error.wordEmpty"] = "The word is empty.",
        ["error.wordDigits"] = "Numbers and words with digits can't be added to exceptions.",
        ["error.wordLetters"] = "The word must contain letters only.",
        ["error.wordAlphabet"] = "Only Russian and English letters are supported.",
        ["error.wordTooShort"] = "The word is too short: at least 2 letters are required.",
        ["error.wordMixed"] = "The word must not mix Russian and English letters.",
        ["error.wordFrequent"] = "This word is in the top 100 most frequent and can't be excluded.",
        ["error.wordGeneric"] = "This word can't be added to exceptions.",
        ["error.wordDuplicateDirection"] = "That word is already in this direction.",
        ["error.wordConflict"] =
            "The word is already excluded in one direction — remove the old entry.",
        ["error.typoBothFields"] = "Fill in both fields: what to look for and what to replace it with.",
        ["error.typoEquals"] = "The \"=\" character separates pairs in the dictionary file and can't be used.",
        ["error.typoSame"] = "The replacement is the same as the original word.",
    };
}
