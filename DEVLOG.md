# Журнал разработки KeySwitch

Формат: этап → что сделано → как проверено → результат. Записи только по факту
(сборка/запуск/тесты), без «должно работать».

## Этап 0. Каркас решения

**Сделано.**
- Создан git-репозиторий, `KeySwitch.sln` (классический формат sln), 7 проектов:
  `Core`, `Data`, `Hooks`, `App`, `Tests`, `tools/Evaluate`, `tools/BuildAssets`.
- `Directory.Build.props`: `net8.0`, `LangVersion 12.0`, nullable, implicit usings,
  `EnableWindowsTargeting`, подавление `CA1416` (P/Invoke-код платформенно-специфичен по определению).
- `App` настроен как `WinExe` с `AssemblyName=KeySwitch`, `win-x64`, self-contained
  single-file publish, манифест и иконка.
- `.gitignore` для `bin/obj/.vs/artifacts/publish/TestResults`.
- `docs/CONTRACTS.md` — контракты слоёв (API, порядок обработки нажатия, формат ассетов).

**Проверка.** `dotnet build src\KeySwitch.Core` — 0 ошибок. `dotnet sln list` — 7 проектов.

## Этап 1. Ядро: таблицы, буфер, детектор, движок (Core)

**Сделано.**
- `Layout`: `KeyStroke`, `LayoutTable`, `LayoutCatalog` (US и ЙЦУКЕН по физическим клавишам,
  включая Shift-регистр и знаки), `LayoutPairRegistry`, `LayoutConverter`.
- `Input`: `WordBuffer` (накопление слова с учётом VK/Shift/CapsLock, Backspace, сброс),
  `KeyClassifier` (роли клавиш: буква, терминатор, backspace, сброс, игнор).
- `Text`: `WordText` (нормализация, разбор хвостовой пунктуации), `NgramHasher`,
  `NgramModelFile` (бинарный формат v1).
- `Detect`: `DetectionSettings`, `ILanguageResources`, `LanguageScorer`,
  `ImpossibleCombinations`, `LayoutDetector` (три гипотезы прочтения, пороги, раннее срабатывание).
- `Correction`: `CorrectionPlan`, `UndoPlan`.
- `Learning`: `ILearningStore`, `IExceptionPolicy`, `NullLearningStore`, `NullExceptionPolicy`.
- `PostProcess`: `IPostProcessor`, `CapsFixer`, `TypoCorrector`, `YoFixer`, `PostProcessorPipeline`.
- `Engine`: `CorrectionEngine` (буфер → детектор → план → отмена → ручная конвертация выделения).

**Проверка.** `dotnet build src\KeySwitch.Core -v q` — 0 ошибок, 0 предупреждений.

**Замечания.** Пороги вынесены в `DetectionSettings` и в `settings.json`; основной защитный
механизм — «A есть в словаре → не переключать» (см. DECISIONS.md, D004).

## Этап 2. Таблицы раскладок и ручная конвертация

**Сделано.**
- `LayoutCatalog`: US и ЙЦУКЕН заданы по физическим клавишам (VK 0x41–0x5A, OEM-клавиши
  0xBA/0xBB/0xBC/0xBD/0xBE/0xBF/0xC0/0xDB/0xDC/0xDD/0xDE, цифровой ряд, пробел), с учётом
  Shift-регистра и знаков (`Shift+4` → `;` в RU, `Shift+3` → `№`, `Shift+7` → `?`).
- `LayoutConverter.Convert(text, from, to)` — конвертация в обе стороны по физическим клавишам,
  неизвестный символ копируется как есть (текст не теряется).
- `LayoutPairRegistry` — пары раскладок; `CorrectionEngine.ConvertManually()` конвертирует
  последнее слово буфера без автопереключения.

**Проверка.** `dotnet test src\KeySwitch.Tests` — тесты `LayoutTableTests`, `LayoutConverterTests`
проходят; ручная конвертация покрыта `CorrectionEngineTests`.

## Этап 3. Хук, переключение раскладки окна и SendInput с очередью

**Сделано.**
- `KeyboardHook`: `WH_KEYBOARD_LL` в отдельном потоке с собственным message loop; Start/Stop/Dispose
  сериализованы `lock`, Stop = 3×(WM_QUIT + Join(200)) + аварийный `UnhookWindowsHookEx` + Join(1000),
  защита от self-join, Start после Dispose → `ObjectDisposedException`.
- Свои инъекции помечаются: `LLKHF_INJECTED` + собственный маркер в `dwExtraInfo`; такие события
  не попадают в буфер и передаются дальше через `CallNextHookEx`.
- `InputSender`: `SendInput` с `KEYEVENTF_UNICODE`, `SendCorrection(backspaces, text)` отправляет
  Backspace и текст **одним** вызовом; `SendKeyStroke` — одно событие down/up с Shift вокруг.
- `LayoutSwitcher`: `WM_INPUTLANGCHANGEREQUEST` в окно foreground + проверка результата через
  `GetKeyboardLayout(threadId)`; для консольных окон адресат — `GetConsoleWindow()`.
- `ForegroundTracker`, `FullscreenDetector`, `ElevationDetector`, `KeyQueue` (нажатия пользователя
  во время исправления не теряются, воспроизводятся после плана), `UiAutomationProbe` (таймаут 30 мс,
  затем пауза 500 мс, кэш 1 с), `ClipboardHelper` на чистом Win32.

**Найденные и исправленные баги.**
- `KeyboardHook.ResolveActiveLayout` брал thread id из out-параметра `GetWindowThreadProcessId`
  (там process id) — символ считался по чужому HKL. Теперь берётся возвращаемое значение.
- WPF `System.Windows.Clipboard` в этом окружении бросает `COMException 0x800401D0 CLIPBRD_E_CANT_OPEN`
  даже на STA с `OleInitialize` → `ClipboardHelper` переписан на Win32 (text, CF_HDROP, DIB, DIBV5,
  HTML, RTF; снимок в JSON, лимит 8 МБ на формат).

**Проверка.** `dotnet build src\KeySwitch.Hooks` — 0 ошибок/0 предупреждений; смоук Start/Stop,
включая гонку из 4 потоков. Живая доставка `SendInput` в чужие окна в сессии агента не
воспроизводится (см. этап 9, ограничения).

## Этап 4. Словари, n-граммы, детектор, автопереключение

**Сделано.**
- `tools/BuildAssets` (единственное место с сетью): LibreOffice `en_US.dic`/`ru_RU.dic` +
  собственный минимальный Hunspell-экспандер (PFX/SFX, FLAG, STRIP, ADD, CONDITION), частотные
  списки hermitdave/FrequencyWords, корпус Tatoeba (rus/eng). Сборка ассетов ~18 с.
- Ассеты 3,7 МБ, 13 embedded-ресурсов: `words-ru` 400 000 слов, `words-en` 98 228 (предел словаря),
  `ngrams-{ru,en}-{3,4}` (12 173 / 62 747 / 9 368 / 51 866), `yo-ru` 30 000 пар,
  `typos-ru` 140 / `typos-en` 173, лицензии.
- `LanguageResources` + `WordDictionary`/`CharNgramModel`, `LayoutDetector` (три гипотезы прочтения
  спорных знаков), `LanguageScorer`, `ImpossibleCombinations`.

**Найденный и исправленный баг.** В `tools/BuildAssets/AffixFile.cs` отрицание `[^...]` в CONDITION
трактовалось как положительный набор — не применялась бо́льшая часть правил `ru_RU.aff`. После
исправления экспандер даёт 1 294 104 словоформы из 146 269 лемм.

**Проверка (tools/Evaluate, реальные ассеты).**

| Прогон | Верных | Ложных | µs/слово |
|---|---|---|---|
| RU forward (555) | 99,64 % (553) | 0,00 % | 8,85 |
| RU reverse (555) | 99,64 % (553) | 0,00 % | 9,32 |
| EN forward (820) | 100,00 % (820) | 0,00 % | 7,54 |

Критерий ≥ 98 % и ≤ 0,5 % выполнен. Остаются 2 непереключённых RU-слова («слаженной», «зашумел»):
их словоформы есть в экспандере, но не вошли в отбор 400 000.

## Этап 5. Отмена и самообучение

**Сделано.**
- Двойной тап `Shift` (< 400 мс, без других клавиш) и `Pause/Break` отменяют последнюю конвертацию:
  возвращается исходный текст и исходная раскладка (`CorrectionEngine.TryUndo`).
- `LearningStore`: по каждому слову хранятся только счётчики (сколько раз отменяли/исправляли),
  без контекста и истории набора; после 3 отмен слово уходит в локальные исключения.
- Одиночный тап `Shift` (опция, по умолчанию выключена) переключает язык с задержкой 400 мс,
  чтобы не конфликтовать с двойным тапом.

**Проверка.** Тесты `LearningStoreTests`, `CorrectionEngineTests` (отмена, повторная отмена,
отмена после ручной конвертации) — проходят.

## Этап 6. Исключения программ и слов, игры, пароли

**Сделано.**
- `ExceptionStore`: 10 процессов по умолчанию (Code.exe, Cursor.exe, devenv.exe, idea64.exe,
  pycharm64.exe, WindowsTerminal.exe, cmd.exe, powershell.exe, pwsh.exe, mstsc.exe), правила по папке
  (`\Games\`), три режима на программу: выключено / только ручная конвертация / полностью.
- Автоотключение в полноэкранных окнах (`FullscreenDetector`: окно покрывает монитор и не является
  рабочим столом).
- Слова-исключения с направлением; нельзя добавить слово из топ-100 частотных и одиночную букву —
  показывается понятное сообщение (`ExceptionStore.CanAddWordException`).
- Пароли: `UiAutomationProbe` проверяет `IsPassword` у focused-элемента; плюс эвристика «длина > 6
  и одновременно строчные + заглавные + цифры/знаки → это пароль» → слово принудительно остаётся
  в английской раскладке.

**Проверка.** Тесты `ExceptionStoreTests` (режимы, папки, запреты), `PasswordHeuristicTests` — проходят.

## Этап 7. Опечатки, «ИСправление», ёфикатор, звуки

**Сделано.**
- `TypoCorrector` — словарь замен в редактируемых `%APPDATA%\KeySwitch\typos-ru.txt` / `typos-en.txt`
  (по умолчанию 140/173 правила), опция выключена по умолчанию.
- `CapsFixer` — «ПРивет» → «Привет» (две заглавные в начале слова + строчные дальше).
- `YoFixer` — ё только в однозначных случаях по словарю 30 000 пар; неоднозначные («все/всё»)
  не трогаются; `еще`/`ее` в whitelist.
- `SoundService` — `MediaPlayer`, свои wav-файлы в `%APPDATA%\KeySwitch\sounds`, громкость 0..1
  без глобального `waveOutSetVolume`.
- `PostProcessorPipeline` — расширяемая цепочка: третий язык добавляется таблицей раскладки,
  словарём и n-граммами без правок ядра.

**Проверка.** Тесты `PostProcessorTests` (опечатки, ПРивет, ё, порядок применения) — проходят.

## Этап 8. Окна настроек WPF

**Сделано.**
- `SettingsWindow` 860×640 без системной рамки, свой заголовок, Esc/Enter, 5 вкладок:
  Общие, Исключения, Опечатки, Звуки, О программе.
- Стиль Windows 11: скруглённые углы (DWM), карточки 12 px, поля 8 px, тумблеры, тёмная/светлая/
  системная тема (`ThemeManager`, тёмный заголовок DWM).
- `SettingsViewModel` с валидацией: порог 0.5–5.0, минимальная длина слова 2–20, горячая клавиша.
- `TrayIconFactory` — иконки EN/RU/выключено/только-ручная в 16/24/32/48, `Resources/keyswitch.ico`.

**Проверка.** Визуальный харнесс рендерит окно в PNG: сняты все 5 вкладок в светлой и тёмной теме,
скролл, узкое окно 760×560, экран валидации. По ходу исправлены невидимые описания (DataTrigger),
обрезанный глиф «▶», добавлен слайдер порога. Отчёт — `docs/notes/ui.md`.

## Этап 9. Установщик, тесты, README, финальная проверка

**Сделано.**
- `installer/KeySwitch.iss` — Inno Setup 6.3+, per-user установка в `{localappdata}\Programs\KeySwitch`,
  ярлыки, автозапуск, русский и английский языки, удаление с сохранением `%APPDATA%\KeySwitch`.
- `publish.ps1` — сборка, тесты, `dotnet publish` одним файлом, zip-архив.
- `README.md` — функции, установка, ограничения, приватность, сборка, ручной чек-лист из 14 сценариев.
- `DECISIONS.md` — 25 решений с обоснованием, `ARCHITECTURE.md` — слои, потоки, формат данных.

**Проверка.**
- `dotnet build KeySwitch.sln -c Release` — 0 ошибок, 0 предупреждений.
- `dotnet test src\KeySwitch.Tests -c Release` — 352/352, 0 пропущено.
- `tools/Evaluate`: RU forward 99,64 %, RU reverse 99,64 %, EN forward 100,00 %, ложные 0,00 %.
- `publish.ps1 -SkipTests` → `artifacts\publish\KeySwitch.exe` 143,9 МБ; запуск: процесс живёт,
  окна нет, лог `KeySwitch запущен` → `Встроенные языковые ресурсы загружены` → `Языковые ресурсы
  загружены` → `Автопереключение запущено`; корректный выход с кодом 0.

**Не проверено вживую (честно).** В сессии агента `SendInput` не доставляется в чужие окна
(воспроизведено на собственном тестовом окне без приложения), поэтому пункты чек-листа про
живой набор в Блокноте, браузере, Word, Telegram, терминале, игре, поле пароля, окно администратора,
быстрый набор и вставку из буфера остаются на ручную проверку человеком. Трей проверен
программно: `Shell_NotifyIconGetRect` возвращает валидный rect, иконка на скриншоте рисуется,
меню создаётся по `WM_CONTEXTMENU` и закрывается, настройки открываются по `NIN_SELECT`.

## Этап 10. Память: 280 → 110 МБ (вне ТЗ, по результатам замера)

Первый замер опубликованного exe показал 280 МБ в простое при цели ≤ 80 МБ. Что сделано:

1. **Отключено сжатие single-file бандла** (`EnableCompressionInSingleFile=false` в csproj и
   `publish.ps1`). Сжатый бандл держит распакованные сборки в памяти: 280 → 160 МБ, но exe
   67 → 144 МБ. Выбран вариант с меньшей памятью.
2. **Компактные словари** (`Utf8WordTable`, `YoDictionary`): managed heap 45,1 → 16,4 МБ,
   качество Evaluate не изменилось.
3. **Отказ от WinForms**: трей переписан на `Shell_NotifyIcon`, иконки — GDI → `CreateIconIndirect`,
   меню — `TrackPopupMenuEx`. Промежуточный WPF-вариант трея (HwndSource + RenderTargetBitmap)
   оказался хуже на ~40 МБ и отброшен.
4. **Компактирующая сборка мусора** после загрузки словарей — возвращает LOH-буферы распаковки.

**Итог.** Простой: WS 110 МБ / Private 114 МБ, роста за 30 с нет; после первого открытия окна
настроек ~190 МБ (WPF поднимает Direct3D). Цель ≤ 80 МБ не достигнута — разбор и что осталось
сделать (вынести WPF из пути простоя процесса) в DECISIONS.md, D023.

## Этап 11. Исправление: полноэкранный браузер считался игрой

**Симптом.** В Chrome автопереключение не работало вообще, в остальных приложениях — работало.

**Причина.** Окно Chrome в F11 (и полноэкранное видео на YouTube, и PWA на весь экран) занимает
ровно весь монитор и не имеет заголовка. `WindowInspector.IsFullscreenWindow`
(`src\KeySwitch.Hooks\WindowInspector.cs:158-180`) возвращал `true`, а
`AutoSwitchService.IsGameOrProtected` считал такое окно игрой и полностью выключал
автопереключение — при включённом детекторе игр (по умолчанию).

**Репро (A/B/C, отдельный профиль Chrome, ввод через настоящий `AutoSwitchService`).**

| Условие | Раскладка | Поле |
|---|---|---|
| A: детектор игр вкл., окно fullscreen | ru-RU → ru-RU | `руддщ ` — конверсии нет |
| B: детектор игр выкл., то же окно | ru-RU → en-US | `hello ` — конверсия есть |
| C: детектор игр вкл., окно windowed | ru-RU → en-US | `hello ` — конверсия есть |

Единственная ломающая переменная — проверка полноэкранного окна, не Chrome. Отдельно проверено,
что в браузере работают `LayoutSwitcher` (HKL `0x4190419`), батч `SendCorrection`
(7×`VK_BACK` + Unicode), адресная строка и `contenteditable`.

**Фикс.** Новый `src\KeySwitch.Hooks\WindowClassification.cs`: список браузерных процессов
(chrome, msedge, firefox, brave, opera, vivaldi, yandex и родственные) и правило
`IsFullscreenGame(info)` — полноэкранное окно считается игрой только вне браузера.
`AutoSwitchService.IsGameOrProtected` вызывает это правило. Правило «папка `\Games\`»
осталось без изменений.

**Проверка.** `dotnet build KeySwitch.sln -c Release` — 0/0; `dotnet test` — **376/376**
(+24 теста `WindowClassificationTests`: браузеры в фуллскрине не игра, остальные процессы —
игра, оконный режим — не игра, регистр и `.exe` в имени).

## Этап 12. Исправление: хук замирал на ~0,8 с в Chrome (лаги и «не работает»)

**Симптом (сообщение пользователя).** После фикса этапа 11 в браузере автопереключение всё равно
не работало, добавились «сильные лаги» при наборе. В Блокноте и остальных приложениях — работало.

**Как искали.** Стенд `%TEMP%\ksprobe\run.ps1`: Chrome с отдельным профилем, `probe.html` с
`<textarea>`, окну выставляется RU-раскладка через `WM_INPUTLANGCHANGEREQUEST`, ввод —
`keybd_event` физическими VK, результат читается через UIA `ValuePattern`. В `AutoSwitchService`
временно добавлены DIAG-логи с таймингами.

Первое открытие: стенд вообще не доходил до конвейера, потому что `OnKeyEvent` игнорирует
события с `LLKHF_INJECTED`, а `keybd_event` ставит этот флаг. Для теста фильтр ослаблен до
`IsOurEvent` — и конверсия в Chrome заработала (поле `руддщ ` → `hello `, HKL `0x04190419` →
`0x04090409`). Значит сам конвейер в браузере исправен.

Второе открытие — по таймингам: `DIAG timing vk=164 get=781`, `DIAG timing vk=72 get=766` —
`ForegroundTracker.Get()` иногда занимает **~770 мс** прямо в колбэке `WH_KEYBOARD_LL`.
Источник — `src\KeySwitch.Hooks\WindowInspector.cs:96-101`: `Process.GetProcessById` +
`Process.MainModule` перечисляет модули процесса. Прямой замер на живом Chrome (pid 19844):
`MainModule.FileName` — **777 мс** (и пустая строка), `QueryFullProcessImageNameW` — **37 мс**.

**Фикс.** `WindowInspector.Inspect` переведён на чистый Win32: путь — `QueryProcessPath`
(`OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` + `QueryFullProcessImageNameW`), имя —
`Path.GetFileNameWithoutExtension`, повышение прав — `ElevationHelper.IsElevatedProcess`.
Все три значения кэшируются по pid на 30 с (до 64 записей). Разбор — DECISIONS.md, D027.

**Проверка (тот же стенд, та же сборка с ослабленным фильтром).**

| | до фикса | после фикса |
|---|---|---|
| доставка 5 клавиш | 815 мс | **42 мс** |
| один `_tracker.Get()` | 766–781 мс | **0 мс** |
| поле после пробела | `руддщ ` (нет конверсии) | `hello `, HKL `0x0409` |
| первое изменение в поле | 848 мс | 76 мс |

**Сборка и тесты.** `dotnet build KeySwitch.sln -c Release` — 0/0; `dotnet test` — **376/376**;
`publish.ps1 -SkipTests` → `artifacts\publish\KeySwitch.exe` 150 842 914 б; смоук опубликованной
сборки — WS 108 МБ / Private 114 МБ, 18 потоков, лог без ошибок.

**Осталось честно сказать.** Проверка шла на сборке с ослабленным фильтром инъекций: обычная
сборка игнорирует синтетический ввод по замыслу, а физической клавиатуры у стенда нет. Поэтому
живой набор в Chrome человеком — единственная проверка, которую нужно сделать руками.


## Этап 13. Обработка нажатий в рабочем потоке, журнал исправления, раскладка по фокусу

**Задача.** По отчёту пользователя: в журнале видно верное решение детектора (Δ=6,23 при пороге 0,88,
режим Full), но после строки «terminator» ничего нет — шаг исправления либо не выполнялся, либо
падал молча. Требовалось: журнал исправления уровня INFO, колбэк хука за единицы миллисекунд,
кэш окна фокуса с прогревом по `EVENT_SYSTEM_FOREGROUND`, порядок «пробел → Backspace × (длина+1) →
раскладка → слово и пробел», пауза 10–15 мс для браузеров/Electron, повтор `SendInput` при
неполной доставке.

**Сделано.**
- `src\KeySwitch.App\Services\AutoSwitchService.cs` — обработка нажатий вынесена в поток
  `KeySwitch.KeyProcessor` через `BlockingCollection<KeyWorkItem>`; колбэк хука только фильтрует
  и кладёт элемент в очередь.
- `src\KeySwitch.App\Services\PlanExecutor.cs` — журнал исправления: старт (`proc`, `hwnd`,
  `backspaces`, целевая раскладка, пауза, уверенность), результат каждого шага
  (`отправлено/принято/ошибка`), смена раскладки (`before -> after ok=`), итоговое время;
  исключения пишутся со стеком.
- `src\KeySwitch.Hooks\InputSender.cs` — `SendResult(Sent, Inserted, LastError)`, повтор одного
  непринятого хвоста, единый маркер `dwExtraInfo = 0x4B53_0001`.
- `src\KeySwitch.App\Services\CorrectionPacing.cs` — пауза 12 мс между стиранием и вводом для
  Chrome/Edge/Firefox/Electron (список процессов).
- `src\KeySwitch.Hooks\ForegroundWatcher.cs` — `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`,
  прогревает кэш окна и UI Automation при смене фокуса, а не на первом нажатии.
- `src\KeySwitch.Hooks\KeyTextResolver.cs` — символ и раскладка по потоку фокуса
  (`GetGUIThreadInfo` → `hwndFocus` → `GetKeyboardLayout`); в колбэке хука Win32 больше нет.
- `src\KeySwitch.Hooks\KeyboardHook.cs` — замер длительности колбэка, предупреждение при ≥ 20 мс.
- `src\KeySwitch.Hooks\UiAutomationProbe.cs` — таймаут проверки пароля 50 мс.

**Проверка.** `dotnet build KeySwitch.sln -c Release` — 0 ошибок / 0 предупреждений;
`dotnet test` — **376/376**; `publish.ps1 -SkipTests` → `artifacts\publish\KeySwitch.exe`
150 862 882 б; смоук — WS 109 МБ / Private 114 МБ, 20 потоков, рост за 3 с отсутствует.

**Стенд на живом Chrome** (до правки раскладки по фокусу, `KEYSWITCH_ALLOW_INJECTED=1`):
`PROBE work vk=71 ch=103 layout=en-US` … → `Исправление: старт proc=chrome hwnd=0x660A2A
backspaces=7 target=ru-RU layout_before=en-US pause=12мс conf=1,00` →
`Backspace отправлено=14 принято=14 ошибка=0` → `раскладка en-US -> ru-RU ok=True` →
`ввод отправлено=14 принято=14 ошибка=0` → `завершено за 42 мс`.

**Найдено стендом на Блокноте.** Приложение считало символ по раскладке окна переднего плана:
в WinUI3 фокус живёт в другом потоке, окно рапортовало `ru-RU`, а печаталось латиницей —
буфер собирался как «привет», детектор видел валидное слово и молчал. Отсюда `KeyTextResolver`
и D029.

**Не сделано.** Живой прогон двух тестов (Блокнот и Chrome) с показом журнала: на машине
в полноэкранном режиме работает Dota 2 и держит фокус, а синтетический ввод уводит пользователя
из игры. Тесты запускаются одной командой, как только игра закрыта:
`powershell -NoProfile -ExecutionPolicy Bypass -File tools\probe\run-ks.ps1 -Target notepad`
и `-Target chrome`.

## Этап 14. KeySwitch DeepSeek: переименование и английский интерфейс (07.11.2026)

**Переименование.** Продукт называется «KeySwitch DeepSeek» (решение D031). Изменились
`AssemblyName` → `KeySwitchDeepSeek`, `Product`/`AssemblyTitle`, `app.manifest`
(`KeySwitchDeepSeek.app`, версия 1.1.0), папка данных `%APPDATA%\KeySwitchDeepSeek`,
установщик `installer\KeySwitchDeepSeek.iss` (per-user в `%LOCALAPPDATA%\Programs\KeySwitchDeepSeek`,
новый `AppId`), значение автозапуска и имя задачи планировщика `KeySwitchDeepSeek`,
мьютекс и событие активации `KeySwitchDeepSeek.*`, `publish.ps1`, стенд `tools\probe\run-ks.ps1`.
Пространства имён и имена проектов остались `KeySwitch.*` — это структура решения, а не бренд.

**Локализация.** `src\KeySwitch.App\Localization\Loc.cs`: `UiLanguage { System, Russian, English }`,
индексатор `Loc.Instance[key]`, `T`/`Format`, `INotifyPropertyChanged` с пустым именем при смене языка.
`Strings.Ui.cs` — 152 ключа × 2 языка, `Strings.Runtime.cs` — 23 ключа трея, баллунов и запуска.
Меню трея собирается заново при каждом открытии, подсказка пересобирается по `PropertyChanged`.
Окно настроек: XAML привязан через `{Binding [key], Source={x:Static loc:Loc.Instance}}`,
сообщения ViewModel хранят ключ и аргументы и переформатируются на лету; списки вариантов
обновляют подписи на месте — пересоздание сбрасывало выбор `ComboBox` и давало `null` в
non-nullable `ExceptionMode`. Язык применяется сохранением настроек, без перезапуска.
По умолчанию — язык Windows (`"system"`), с ru для ru/uk/be/kk/ky.

**Проверка.** `dotnet build KeySwitch.sln -c Release` — 0 ошибок / 0 предупреждений;
`dotnet test` — **376/376**; `publish.ps1 -SkipTests` → `artifacts\publish\KeySwitchDeepSeek.exe`.
Харнесс `%TEMP%\ksui` отрисовал 5 вкладок в RU и EN, светлую и тёмную темы, 860×640 и 760×560;
скриншоты `docs\img\settings-en.png` и `docs\img\about-en-dark.png`.

**Не сделано.** Установщик не скомпилирован — Inno Setup на машине нет.
