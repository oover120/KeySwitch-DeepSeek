# Контракты между слоями KeySwitch

Документ фиксирует публичные API слоёв. Любой слой можно менять внутри, но эти сигнатуры
обязаны совпадать — на них завязаны остальные проекты и тесты.

## Общая схема

```
KeySwitch.Hooks  ──(события клавиатуры, окно, раскладка, SendInput)──►  KeySwitch.App
                                                                          │
KeySwitch.Core  ◄──(движок, детектор, буфер, планы исправлений)───────────┤
KeySwitch.Data  ◄──(настройки, словари, n-граммы, исключения, обучение)───┘
```

`KeySwitch.Core` не ссылается ни на что, кроме BCL. Все Windows-зависимости — только в Hooks/App.

## KeySwitch.Core (готово)

- `Layout.KeyStroke(int VirtualKey, bool Shift, bool CapsLock, int ScanCode, bool Extended)`
- `Layout.LayoutTable` — `Id`, `DisplayName`, `Language`, `TryMap(vk, shift, out char)`,
  `TryGetKey(char, out vk, out shift)`, `Contains(char)`.
- `Layout.LayoutCatalog` — `EnUs` (`"en-US"`), `RuRu` (`"ru-RU"`), `All`, `TryGet(string?)`.
- `Layout.LayoutPairRegistry` — `CreateDefault()`, `GetAlternate(LayoutTable)`, `AddPair(a,b)`.
- `Layout.LayoutConverter.Convert(string, LayoutTable from, LayoutTable to)`.
- `Input.WordBuffer` — `Append(KeyStroke, char, string layoutId)`, `Backspace()`, `Clear()`,
  `Text`, `Length`, `IsEmpty`, `LayoutId`, `ToToken()`.
- `Input.KeyClassifier.Classify(vk, ctrl, alt, win, char?) → KeyRole`
  (`WordChar`, `Terminator`, `Backspace`, `Reset`, `Ignore`), константы VK.
- `Detect.DetectionSettings` — пороги и веса (см. файл).
- `Detect.ILanguageResourcesProvider` — `GetDictionary(lang)`, `GetTrigrams(lang)`, `GetQuadgrams(lang)`.
- `Detect.LayoutDetector(resources, pairs, settings)` — `Decide(text, source)`, `Decide(text, source, target, early)`.
- `Correction.CorrectionPlan(Kind, OriginalText, ReplacementText, DeleteCount, char? Terminator, SourceLayoutId, TargetLayoutId, Confidence, Reason)`,
  `CorrectionPlan.TextToType`.
- `Correction.UndoPlan(...)` + `TextToType`.
- `Learning.ILearningStore`, `Learning.IExceptionPolicy`, `WordVerdict`, `NullLearningStore`, `NullExceptionPolicy`.
- `PostProcess.IPostProcessor`, `ITypoDictionary`, `IYoDictionary`, `CapsFixer`, `TypoCorrector`, `YoFixer`, `PostProcessorPipeline`.
- `Engine.CorrectionEngine(detector, settings, learning, exceptions, postProcessors)`:
  - `Buffer`, `LastApplied`, `CanUndo`, `LastWord`
  - `AppendChar(KeyStroke, char, string layoutId)`, `Backspace()`, `Reset()`
  - `TryEarlySwitch() → CorrectionPlan?`
  - `CompleteWord(char? terminator, bool terminatorAlreadyTyped) → EngineResult(Word, Plan, Note)`
  - `ConvertManually() → EngineResult`
  - `ConvertSelection(string text, string layoutId) → CorrectionPlan?`
  - `TryUndo(out UndoPlan?)`, `ClearUndo()`
- `Text.NgramModelFile.Write(Stream, order, IEnumerable<KeyValuePair<ulong,float>>)` / `Read(Stream) → NgramModelData`,
  `Text.NgramHasher.Hash/Hash(string)/ForEachGram(word, order, Action<ulong>)`.

## KeySwitch.Data — реализует (namespace `KeySwitch.Data`)

```csharp
public sealed class AppPaths {
    public static AppPaths Default { get; }          // %APPDATA%\KeySwitch
    public string Root { get; }                      // ...\KeySwitch
    public string SettingsFile { get; }              // settings.json
    public string ExceptionsFile { get; }            // exceptions.json
    public string LearningFile { get; }              // learning.json
    public string TyposRuFile { get; }               // typos-ru.txt
    public string TyposEnFile { get; }               // typos-en.txt
    public string LogFile { get; }                   // logs\keyswitch.log
    public string SoundsDir { get; }                 // sounds
    public void EnsureCreated();
}

public sealed class AppSettings { /* свойства ниже */ public AppSettings Clone(); }

public sealed class SettingsStore {
    public AppSettings Current { get; }
    public SettingsStore(AppPaths paths, AppLogger logger);
    public void Load();                              // создаёт файл по умолчанию, чинит битый JSON
    public void Save();
    public event EventHandler? Saved;
}

public sealed class LanguageResources : ILanguageResourcesProvider, IDisposable {
    public static LanguageResources Load(AppLogger logger);   // читает встроенные Assets
    public IWordDictionary? GetDictionary(string language);
    public ICharNgramModel? GetTrigrams(string language);
    public ICharNgramModel? GetQuadgrams(string language);
    public bool IsLoaded { get; }
}

public sealed class ExceptionStore : IExceptionPolicy {
    public List<ProcessRule> Processes { get; }
    public List<WordException> Words { get; }
    public ExceptionStore(AppPaths paths, AppLogger logger, LanguageResources? resources);
    public void Load(); public void Save();
    public WordVerdict CheckWord(string word, string sourceLayoutId, string targetLayoutId);
    public bool CanAddWordException(string word, out string error);
    public void AddWord(string word, string direction, bool fromLearning = false);
    public void RemoveWord(WordException item);
    public ExceptionMode GetMode(string processName, string processPath);
    public void SetMode(string processName, ExceptionMode mode);
    public static IReadOnlyList<ProcessRule> CreateDefaultProcesses();
}

public sealed class LearningStore : ILearningStore {
    public LearningStore(AppPaths paths, AppLogger logger, AppSettings settings);
    public void Save(); public void Load();
    public int TotalUndos { get; } public int TotalAccepted { get; }
    public IReadOnlyDictionary<string, WordLearningStats> Snapshot();
}

public sealed class TypoDictionary : ITypoDictionary {
    public TypoDictionary(AppPaths paths, AppLogger logger);
    public void Reload(); public int Count { get; } public string? LastError { get; }
}

public sealed class YoDictionary : IYoDictionary { public int Count { get; } }

public sealed class AppLogger {
    public AppLogger(AppPaths paths, bool safeMode);
    public void Info(string message); public void Warn(string message);
    public void Error(string message, Exception? ex = null); public void Debug(string message);
    public void Flush();
}
```

`AppSettings` (JSON, camelCase, комментарии — в DECISIONS.md):

```
Enabled, AutoSwitch, EarlySwitchEnabled, EarlySwitchMinLength, SingleShiftSwitch,
DoubleShiftUndo, ManualConvertHotkey, PauseHotkeyEnabled, StartWithWindows, RunAsAdmin,
ConfidenceThreshold, NoDictionaryThresholdMultiplier, EarlySwitchThreshold,
TyposEnabled, CapsFixEnabled, YoFixEnabled, SoundsEnabled, SwitchSoundPath, FixSoundPath,
SoundVolume, GameDetectionEnabled, PasswordProtectionEnabled, SafeMode, Theme, Language,
LearningEnabled, UndoThresholdToSuppress (по умолчанию 3)
```

Ассеты (встраиваемые ресурсы `KeySwitch.Data.Assets`, генерируются tools/BuildAssets):

| Файл | Формат |
|---|---|
| `words-en.txt.gz`, `words-ru.txt.gz` | строки `слово<TAB>частота`, слово в нижнем регистре |
| `ngrams-en-3.bin`, `ngrams-en-4.bin`, `ngrams-ru-3.bin`, `ngrams-ru-4.bin` | `NgramModelFile` v1 |
| `yo-ru.txt.gz` | строки `слово_с_е<TAB>слово_с_ё` |
| `typos-ru.txt`, `typos-en.txt` | `неверно=верно`, `#` — комментарий |
| `licenses/*.txt` | тексты лицензий словарей и корпусов |

## KeySwitch.Hooks — реализует (namespace `KeySwitch.Hooks`)

```csharp
public sealed class KeyboardHookEventArgs : EventArgs {
    public int VirtualKey { get; } public int ScanCode { get; }
    public bool IsKeyDown { get; } public bool IsInjected { get; } public bool IsOurEvent { get; }
    public bool Shift { get; } public bool Ctrl { get; } public bool Alt { get; } public bool Win { get; }
    public bool CapsLock { get; } public char? Character { get; }
    public bool Handled { get; set; }        // true — событие не доходит до приложения
}

public sealed class KeyboardHook : IDisposable {
    public event EventHandler<KeyboardHookEventArgs>? KeyEvent;
    public void Start(); public void Stop(); public bool IsRunning { get; }
}

public sealed class InputSender : IDisposable {
    public void SendBackspaces(int count);
    public void SendText(string text);                  // KEYEVENTF_UNICODE
    public void SendTerminator(char ch);                // space/enter/tab через VK, прочее — Unicode
    public void SendKeyStroke(int virtualKey, bool shift, bool keyUp);
    public void SendShiftTap();                          // для переключения языка
}

public sealed record ForegroundWindowInfo(
    IntPtr Handle, int ProcessId, string ProcessName, string ProcessPath,
    string LayoutId, bool IsElevated, bool IsFullscreen, bool IsConsole);

public sealed class WindowInspector {
    public ForegroundWindowInfo GetForeground();
    public bool TryGetForeground(out ForegroundWindowInfo info);
}

public sealed class LayoutSwitcher {
    public string GetLayoutId(IntPtr window);            // HKL → "ru-RU" / "en-US" / "xx-XX"
    public bool SwitchTo(IntPtr window, string layoutId); // WM_INPUTLANGCHANGEREQUEST + ActivateKeyboardLayout
    public bool Ensure(IntPtr window, string layoutId);
}

public sealed class UiAutomationProbe {
    public bool IsPasswordField(IntPtr window);          // UIA IsPassword, кэш ~1 с
    public bool IsTextInput(IntPtr window);
}

public static class ElevationHelper {
    public static bool IsProcessElevated { get; }
    public static bool IsRunKeyAutostartEnabled();
    public static void SetRunKeyAutostart(bool enabled);
    public static bool IsScheduledAutostartEnabled();
    public static bool EnableScheduledAutostart(bool runAsAdmin);
    public static void DisableScheduledAutostart();
}

public sealed class ClipboardHelper {
    public string? CaptureAll();                         // сериализованный снимок всех форматов
    public void RestoreAll(string? snapshot);
    public string? GetText(); public void SetText(string text);
}
```

Все синтетические события помечаются `dwExtraInfo = InputSender.InjectionMarker` и `LLKHF_INJECTED`;
хук их не отдаёт в `KeyEvent` (только выставляет `IsOurEvent` для отладки).

## Порядок обработки нажатия (KeySwitch.App)

1. `KeyEvent` приходит синхронно из хука; обработчик обязан быть быстрым (< 5 мс).
2. Если окно в списке исключений/полноэкранная игра/поле пароля — по режиму
   (`Disabled` — игнор, `ManualOnly` — только ручные горячие клавиши, `Full` — всё).
3. `KeyClassifier.Classify(...)`:
   - `WordChar` → `engine.AppendChar(...)`, затем (опция) `engine.TryEarlySwitch()`; при плане — выполнить асинхронно;
   - `Terminator` → если буфер не пуст: `engine.CompleteWord(ch, terminatorAlreadyTyped: false)`;
     если план есть — `args.Handled = true` (клавиша глушится) и план уходит в очередь выполнения;
     если плана нет — событие проходит как обычно;
   - `Backspace` → `engine.Backspace()`; `Reset` → `engine.Reset()`.
4. Двойной тап Shift (< 400 мс, без других клавиш) → отмена последнего исправления.
   Одиночный тап Shift (опция) → смена языка.
5. Pause/Break: если `engine.CanUndo` — отмена, иначе `engine.ConvertManually()`.
6. Ctrl+Shift+Space — конвертация выделения через буфер обмена.
7. Во время выполнения плана новые нажатия пользователя складываются в очередь и
   воспроизводятся после исправления (чтобы быстрый набор не терялся).

## Выполнение плана

```
await Task.Run(() => {
    if (targetLayout != sourceLayout) layoutSwitcher.SwitchTo(hwnd, targetLayout);  // до ввода
    sender.SendBackspaces(plan.DeleteCount);
    sender.SendText(plan.TextToType);   // Terminator входит в TextToType
});
```
Backspace и текст отправляются одним пакетом `SendInput`; между ними не должно быть
пользовательских событий (очередь).

## Сборка и тесты

```
dotnet build KeySwitch.sln -c Debug
dotnet test src/KeySwitch.Tests
dotnet run --project tools/Evaluate -- --corpus <path>
```
