# App-слой KeySwitch — заметки

Автор: app-dev. Зона: `src/KeySwitch.App/App.xaml`, `App.xaml.cs`, `Services/**`, `app.manifest`, `installer/**`, `publish.ps1`.
Статус на момент записи: весь app-слой написан и интегрирован с Core/Data/Hooks и UI-слоем; `dotnet build src/KeySwitch.App` — 0 ошибок / 0 предупреждений; `dotnet test src/KeySwitch.Tests` — 352 теста проходят; запуск, single-instance, окно настроек, сохранение и корректное завершение проверены на живом процессе.

## Решения

### Манифест и DPI
- `app.manifest`: `requestedExecutionLevel = asInvoker` (не requireAdministrator — повышение прав отдельной опцией через задачу планировщика), `dpiAware = true/pm` + `dpiAwareness = PerMonitorV2, PerMonitor`, `longPathAware = true`, `supportedOS` Win10/11 (`{8e0f7a12-...}`) и старые версии для совместимости.
- Для WPF per-monitor v2 берётся только из манифеста; предупреждение WinForms-анализатора `WFAC010` подавлено в csproj комментарием (WinForms используется только для трея, своего DPI-режима не задаёт).

### Старт и single instance
- `App.xaml`: без `StartupUri`, `ShutdownMode = OnExplicitShutdown` — окон при старте нет, приложение живёт в трее до явного `Shutdown()`.
- `App.xaml.cs`: `SingleInstance` = `Mutex Global\KeySwitch.SingleInstance` (+ fallback `Local\`, если `Global\` запрещён политикой) и именованный `EventWaitHandle` `Global\KeySwitch.Activate`. Второй экземпляр поднимает событие и выходит (проверено: второй процесс завершается за ~160 мс с кодом 0), первый получает `ActivationRequested` и открывает окно настроек.
- Порядок `OnStartup`: захват mutex → подписка на активацию **и `StartListening()`** (без него событие никто не ждал — найденный и исправленный баг) → `AppPaths.Default.EnsureCreated()` → `AppLogger` → глобальные обработчики исключений → `AppHost` → `ThemeManager.EnsureInitialized(_host.Settings.Theme)` до любых окон → `_host.Start()`.
- `OnExit`: `AppHost.Dispose()` (хук, исполнитель, трей, сохранение настроек), `SingleInstance.Dispose()`, запись «Приложение остановлено.», `Flush()` лога.
- Глобальные исключения: `AppDomain.UnhandledException`, `DispatcherUnhandledException` (`Handled = true`), `TaskScheduler.UnobservedTaskException` → `AppLogger.Error`; приложение не падает. Ошибка старта — MessageBox с текстом + `Shutdown()`.

### Инициализация (AppHost)
Порядок и почему:
1. `SettingsStore` + `Load()` — настройки нужны всем сервисам.
2. `ExceptionStore` (с `resources: null`, словари подключаются позже), `LearningStore`, `TypoDictionary`, `YoDictionary`.
3. `DeferredLanguageProvider` (пустой до загрузки словарей) → `LayoutPairRegistry.CreateDefault()` → `DetectionSettings` из `AppSettings` → `LayoutDetector` → `PostProcessorPipeline` (CapsFixer/TypoCorrector/YoFixer, каждый обёрнут в `ToggleablePostProcessor` и уважает свои флаги) → `CorrectionEngine`.
4. `KeyboardHook`, `InputSender`, `LayoutSwitcher`, `ClipboardHelper`, `ForegroundTracker` (`WindowInspector` + `UiAutomationProbe`), `PlanExecutor`, `SoundService` (+ `Shared`, громкость из настроек).
5. `AutoSwitchService`, `AutostartService`, `TrayService` и подписки на его события.
- `LanguageResources.Load` — синхронная и тяжёлая операция (~0.3–1 с), поэтому выполняется в `Task.Run`, затем `DeferredLanguageProvider.Inner` подставляется, `ExceptionStore.AttachResources` + `WarmFrequentWords` и только после этого **стартует хук** (`AutoSwitchService.Start()` на UI-диспетчере). Так детектор никогда не работает без словарей, а трей и настройки доступны сразу.
- `OpenSettings()` лениво создаёт `SettingsViewModel` (один на приложение), подписывается на `Saved`; повторное открытие вызывает `Reload()`. Окно — `ShowDialog()`, защита от двойного открытия через `IsLoaded`.
- `OnSettingsSaved` (после `SettingsViewModel.Apply()`): `SettingsStore.Save()`, `SoundService.SetVolume`, `CorrectionEngine.ClearUndo`, `AutoSwitchService.Reconfigure()` (порог уверенности, раннее переключение, хоткей), `ThemeManager.Apply(theme)`, `AutostartService.Apply()`, `TrayService.RefreshNow()`.

### AutoSwitchService (обработка нажатий)
- Синхронная часть в колбэке хука: классификация `KeyClassifier`, буфер `CorrectionEngine`, терминатор → `CompleteWord`, раннее срабатывание, хоткеи; всё, что печатает/переключает — уходит в `PlanExecutor` (поток), колбэк не блокируется.
- Инъекции самого приложения (`IsInjected`/`IsOurEvent`) игнорируются; пока исполнитель занят, нажатия пользователя складываются в очередь `PlanExecutor` и воспроизводятся после плана (быстрый набор не теряется).
- Терминаторы после правки Core (lead): `CompleteWord(terminator, terminatorAlreadyTyped: !swallowed)` — терминатор передаётся **всегда**. Пробел/пунктуация уже доставлены окну, поэтому `DeleteCount = text.Length + 1` и исполнитель печатает `ReplacementText` + терминатор; Enter/Tab перехватываются (`Handled = true`), в плане `Terminator != null`, `DeleteCount = text.Length`, исполнитель печатает и символ Enter/Tab через VK. Костыль с `_pendingTerminator` удалён — движок сам учитывает терминатор в `UndoPlan`.
- Раннее срабатывание (`EarlySwitchEnabled`): при уверенном детекте на середине слова клавиша перехватывается и `DeleteCount` уменьшается на 1 — на экране текущего символа ещё нет, а `ReplacementText` его уже содержит. Без перехвата наши Backspace могли обогнать доставку символа окну.
- `Pause/Break` (при `PauseHotkeyEnabled`): сначала отмена последнего исправления (`TryUndo`), если отменять нечего — ручная конвертация последнего слова (`ConvertManually`).
- Двойной Shift (< 400 мс, без других клавиш) — отмена последнего исправления; одиночный Shift (опция `SingleShiftSwitch`) — переключение раскладки текущего окна. Если включены оба жеста, одиночный Shift откладывается таймером на 400 мс, чтобы не сработать раньше двойного.
- `Ctrl+Shift+Space` (`ManualConvertHotkey`, настраивается, пустая строка = выкл.) — конвертация выделения: снимок буфера обмена → очистка → `Ctrl+C` → ожидание текста (до 500 мс) → `ConvertSelection` → `LayoutSwitcher.Ensure` → `SetText` → `Ctrl+V` → восстановление снимка буфера; пустой/недоступный буфер = «выделения нет» → конвертация последнего слова. Выделение исполняется на STA-потоке `PlanExecutor` (`ClipboardHelper` требует STA).
- Пауза по горячей клавише — **Ctrl+Alt+P** (фиксированная, см. «Отклонения»).
- Режимы исключений (`ExceptionMode.Disabled/ManualOnly/Full`): `Disabled` — сервис не трогает клавиатуру; `ManualOnly` — буфер ведётся, но автопланы не исполняются (работает только ручная конвертация/отмена); `Full` — полное автопереключение. Полноэкранные приложения (`GameDetectionEnabled`), пути с `\Games\`, повышенное окно при обычном процессе (UIPI) — автопереключение отключается.
- Защита паролей: `ForegroundTracker.IsPasswordField` (UIA, кэш 2 с) — буфер сбрасывается, переключения не выполняются; эвристика «похоже на пароль» (длина > 6 и одновременно строчные + заглавные + цифры/знаки) тоже оставляет текст как набран.
- Отключение/включение автопереключения — из меню трея (флаг `AppSettings.Enabled`), `Reconfigure()` перечитывает настройки без перезапуска хука.

### PlanExecutor
- Отдельный STA-поток с очередью; `ExecuteExclusive` захватывает флаг `IsBusy`, хук на это время блокирует пользовательский ввод (`TryBlockKey → Handled = true`) и складывает его в очередь; после плана очередь воспроизводится через `SendInput`, и только потом снимается `IsBusy`. Порядок «переключить раскладку → Backspace → текст → терминатор» соответствует `docs/CONTRACTS.md`.
- Undo исполняется тем же путём: `SwitchTo(RestoreLayoutId)` → Backspace → `RestoreText` → терминатор.

### Трей (TrayService)
- `NotifyIcon` + WinForms `ContextMenuStrip` на UI-потоке; таймер 500 мс обновляет иконку/тултип по текущей раскладке, состоянию автопереключения и ручному режиму. Иконка — из `TrayIconFactory.Create(layoutId, enabled, manualOnly)`, старая уничтожается после замены.
- Меню: «Автопереключение включено/выключено», «Исключения…», «Настройки…», «Запускать при входе в Windows», «Запускать от администратора» (активен только при включённом автозапуске), «Выход».
- Тултип: `KeySwitch · RU · автопереключение включено` (варианты «выключено», «ручной режим», «безопасный режим»).
- UIPI: если переднее окно повышено, а процесс нет — баллун «Перехват клавиатуры не работает в окнах с правами администратора», один раз на HWND (HashSet).
- Смена состояния из потока хука приходит через захваченный `SynchronizationContext.Post` — WinForms-объекты не трогаются из чужого потока.

### Звук (SoundService)
- `MediaPlayer` (WPF) на диспетчере, по одному кэшированному плееру на путь, `Volume = AppSettings.SoundVolume` (0..1). `SoundPlayer` не умеет громкость, а Win32 `waveOutSetVolume` глобальный — MediaPlayer проще и не влияет на другие приложения. Воспроизведение только при `SoundsEnabled` и непустом пути; ошибки логируются, не пробрасываются.

### Автозапуск (AutostartService)
- Два способа: Run-ключ HKCU (`ElevationHelper.SetRunKeyAutostart`) для обычного запуска и задача планировщика (`EnableScheduledAutostart(runAsAdmin)`), которая нужна для запуска с повышенными правами.
- `Synchronize()` при старте: если автозапуск в ОС уже есть, а `StartWithWindows = false` (например, включён установщиком) — «усыновляет» его (пишет `true` в настройки), иначе установщик не смог бы включить автозапуск; если настройка включена, а записи нет — создаёт.
- `RunAsAdmin` можно включить только из уже повышенного процесса (задача `RL HIGHEST`); иначе настройка откатывается и показывается предупреждение.

### Установка и публикация
- `installer/KeySwitch.iss`: Inno Setup 6 (**6.3+**: используется `x64compatible`), per-user (`{localappdata}\Programs\KeySwitch`, `PrivilegesRequired=lowest`), языки ru+en, ярлык в меню «Пуск» (рабочий стол — задача, по умолчанию выкл.), автозапуск через HKCU Run (задача, по умолчанию выкл.), удаление через uninstall, `CloseApplications=yes`. `%APPDATA%\KeySwitch` при удалении **не** удаляется (настройки/словари пользователя).
- `publish.ps1`: чистит `artifacts\publish`, при необходимости гоняет тесты, затем `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=embedded`; проверяет наличие `KeySwitch.exe`, печатает размер; `-Zip` упаковывает архив.
- `installer` рассчитан на результат `artifacts\publish` — сначала `publish.ps1`, потом компиляция `.iss`.

## Отклонения

- «Исключения…» в меню трея открывает окно настроек целиком: у `SettingsWindow`/`SettingsViewModel` нет API выбора вкладки (контракт с UI этого не предусматривал), а дублировать окно исключений не хотелось.
- Пауза зафиксирована как **Ctrl+Alt+P**: в `AppSettings` нет строкового поля для этой горячей клавиши, а `Alt+Shift` запрещён заданием. Pause/Break дублирует её при `PauseHotkeyEnabled`.
- Раннее переключение перехватывает текущую клавишу (`Handled = true`) и уменьшает `DeleteCount` — это отличается от формулировки «событие не глушится» в задании, но иначе наши Backspace могут выполниться раньше, чем окно получит символ.
- Одиночный Shift при включённом двойном Shift откладывается таймером на 400 мс — иначе жесты конфликтуют.
- Хук стартует только после загрузки языковых словарей (трей и настройки доступны раньше). Пока словари грузятся, автопереключение не работает — это осознанный выбор: детектор без словарей даёт ложные срабатывания.
- `SafeMode` не запускает хук вообще (только трей и настройки) — так задумано для диагностики.
- `%APPDATA%\KeySwitch` не удаляется деинсталлятором.
- В csproj подавлен `WFAC010` (см. «Манифест и DPI»).
- Установщик не компилировался: Inno Setup в системе может отсутствовать, по заданию компиляцию не запускал.
- `publish.ps1` намеренно написан **латиницей (ASCII)**: Windows PowerShell 5.1 читает UTF-8 без BOM как ANSI, и кириллица в нём ломала разбор строк (байты 0x91/0x92 превращаются в «умные кавычки»). ASCII-скрипт одинаково работает и в PowerShell 5.1, и в PowerShell 7, но сообщения в консоли английские. `.iss` и остальные файлы — обычные UTF-8 без BOM, с русским текстом.

## Не сделано / что проверить

- **Живой ввод в Блокноте не проверен автоматически.** В сессии агента синтетический ввод (`SendInput`) не доходит до окон: это воспроизведено на собственном тестовом WinForms-окне без приложения (клавиши не появляются в TextBox), поэтому сценарии «ghbdtn + пробел → привет », «Ghbdtn + Enter → Привет + перевод строки» подтверждены юнит-тестами движка (352 теста, терминатор/undo) и кодом `HandleTerminator`, но требуют ручной проверки на живом рабочем столе.
- Трей визуально не проверен (иконка, меню, тултип, баллуны UIPI): проверено только, что процесс живёт, иконка создаётся, меню строится; нужен ручной взгляд в область уведомлений.
- Не проверены вживую: определение поля пароля через UIA, отключение в полноэкранных играх, конвертация выделения через Ctrl+Shift+Space (нужен реальный текстовый редактор и выделение).
- Автозапуск (Run-ключ и задача планировщика) не проверялся перезагрузкой; `RunAsAdmin` требует запуска приложения от администратора — из обычного процесса только предупреждение.
- Установщик `.iss` не компилировался и не устанавливался.
- Нет CLI-аргументов: второй экземпляр всегда открывает окно настроек (нельзя, например, `--toggle`).
- Проверено: запуск без окон, создание `%APPDATA%\KeySwitch` (settings.json, exceptions.json, typos-*.txt, logs), single-instance + активация окна настроек, сохранение из окна (`Saved` → `settings.json` + переконфигурация), корректное завершение (`Shutdown` → Dispose всех сервисов, «KeySwitch завершил работу», «Приложение остановлено», код 0).

## Как проверить вручную

```
dotnet build src/KeySwitch.App -v q
powershell -NoProfile -ExecutionPolicy Bypass -File .\publish.ps1 -SkipTests   # или pwsh 7
src\KeySwitch.App\bin\Debug\net8.0-windows\win-x64\KeySwitch.exe
```
1. В трее появилась иконка; второй запуск exe открывает настройки первого.
2. В Блокноте (раскладка EN): `ghbdtn` + пробел → `привет `; `Ghbdtn` + Enter → `Привет` + перевод строки; двойной Shift — отмена; Pause/Break — отмена/ручная конвертация; Ctrl+Alt+P — пауза автопереключения.
3. Выделить текст в редакторе, Ctrl+Shift+Space — конвертация выделения, буфер обмена восстановлен.
4. Меню трея: «Настройки…» → сохранить → настройки применились без перезапуска.
5. «Выход» — процесс завершается, в логе «Приложение остановлено.».
