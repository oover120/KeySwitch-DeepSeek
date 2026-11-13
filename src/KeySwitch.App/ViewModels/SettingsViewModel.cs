using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Data;
using System.Windows.Input;
using KeySwitch.App.Localization;
using KeySwitch.App.Services;
using KeySwitch.Data;

namespace KeySwitch.App.ViewModels;

/// <summary>
/// ViewModel окна настроек. Работает с переданными экземплярами Data: настройки
/// копируются в AppSettings при Apply(), исключения и опечатки сохраняются сразу.
/// Все видимые пользователю строки берутся из <see cref="Loc"/>; при смене языка
/// списки вариантов пересоздаются, а сообщения переформатируются без перезапуска окна.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private readonly ExceptionStore _exceptions;
    private readonly LearningStore _learning;
    private readonly AppPaths _paths;
    private readonly AppLogger _logger;
    private readonly TypoDictionary _typos;

    // Снимок словаря опечаток на момент Reload — чтобы Apply умел удалять записи.
    private readonly Dictionary<string, string> _typosRuSnapshot = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _typosEnSnapshot = new(StringComparer.OrdinalIgnoreCase);

    private bool _enabled;
    private bool _autoSwitch;
    private bool _earlySwitchEnabled;
    private int _earlySwitchMinLength;
    private bool _singleShiftSwitch;
    private bool _doubleShiftUndo;
    private bool _gameDetectionEnabled;
    private double _confidenceThreshold;
    private bool _startWithWindows;
    private bool _runAsAdmin;
    private bool _safeMode;
    private string _themeValue = "system";
    private string _languageValue = "ru";
    private string _manualConvertHotkey = string.Empty;
    private bool _pauseHotkeyEnabled;

    private bool _typosEnabled;
    private bool _capsFixEnabled;
    private bool _yoFixEnabled;
    private string _selectedTypoLanguage = "ru";
    private string _newTypoWrong = string.Empty;
    private string _newTypoRight = string.Empty;

    private bool _soundsEnabled;
    private string _switchSoundPath = string.Empty;
    private string _fixSoundPath = string.Empty;
    private double _soundVolume = 0.7;

    private string _newProcessName = string.Empty;
    private ExceptionMode _newProcessMode = ExceptionMode.Full;
    private string _newWord = string.Empty;
    private string _newWordDirection = ExceptionStore.DirectionBoth;

    // Ошибки ввода храним ключами — при смене языка они переформатируются.
    private string? _processErrorKey;
    private string? _wordErrorKey;
    private string? _typoErrorKey;
    private string? _statusKey;
    private object?[] _statusArgs = Array.Empty<object?>();
    private string? _validationKey;
    private object?[] _validationArgs = Array.Empty<object?>();
    private bool _hasValidationError;

    private string _versionText = "1.0.0";
    private string? _licensesRaw;
    private int _suppressedWords;

    public SettingsViewModel(
        AppSettings settings,
        ExceptionStore exceptions,
        LearningStore learning,
        AppPaths paths,
        AppLogger logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _exceptions = exceptions ?? throw new ArgumentNullException(nameof(exceptions));
        _learning = learning ?? throw new ArgumentNullException(nameof(learning));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _typos = new TypoDictionary(paths, logger);

        Themes = OptionLists.CreateThemes();
        Languages = OptionLists.CreateLanguages();
        Directions = OptionLists.CreateDirections();
        TypoLanguages = OptionLists.CreateTypoLanguages();
        Modes = OptionLists.CreateModes();

        Processes = new ObservableCollection<ProcessRow>();
        Words = new ObservableCollection<WordRow>();
        TypoEntries = new ObservableCollection<TypoRow>();

        TypoView = CollectionViewSource.GetDefaultView(TypoEntries);
        TypoView.Filter = item => item is TypoRow row && row.Language == SelectedTypoLanguage;

        AddProcessCommand = new RelayCommand(AddProcess);
        BrowseProcessCommand = new RelayCommand(BrowseProcess);
        RemoveProcessCommand = new RelayCommand(parameter => RemoveProcess(parameter as ProcessRow));
        AddWordCommand = new RelayCommand(AddWord);
        RemoveWordCommand = new RelayCommand(parameter => RemoveWord(parameter as WordRow));
        BrowseSwitchSoundCommand = new RelayCommand(() => BrowseSound(isSwitch: true));
        BrowseFixSoundCommand = new RelayCommand(() => BrowseSound(isSwitch: false));
        PlaySwitchSoundCommand = new RelayCommand(() => PlaySound(SwitchSoundPath));
        PlayFixSoundCommand = new RelayCommand(() => PlaySound(FixSoundPath));
        AddTypoCommand = new RelayCommand(AddTypo);
        RemoveTypoCommand = new RelayCommand(parameter => RemoveTypo(parameter as TypoRow));
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);
        OpenReadmeCommand = new RelayCommand(OpenReadme);
        CheckUpdatesCommand = new RelayCommand(() => UpdateService.CheckInBackground(null));

        _licensesRaw = LoadLicenses();
        VersionText = ReadVersion();

        // Окно настройки переиспользует один экземпляр VM, поэтому подписка живёт
        // столько же, сколько приложение (см. AppHost).
        Loc.Instance.PropertyChanged += OnLocalizationChanged;

        Reload();
    }

    /// <summary>Настройки успешно сохранены (app-dev подхватывает и применяет рантайм).</summary>
    public event EventHandler? Saved;

    public ObservableCollection<ProcessRow> Processes { get; }

    public ObservableCollection<WordRow> Words { get; }

    public ObservableCollection<TypoRow> TypoEntries { get; }

    public ICollectionView TypoView { get; }

    //────────────────────── Списки вариантов (локализуемые) ──────────────────────

    public IReadOnlyList<ChoiceOption> Themes { get; private set; }

    public IReadOnlyList<ChoiceOption> Languages { get; private set; }

    public IReadOnlyList<ChoiceOption> Directions { get; private set; }

    public IReadOnlyList<ChoiceOption> TypoLanguages { get; private set; }

    public IReadOnlyList<ModeOption> Modes { get; private set; }

    public ICommand AddProcessCommand { get; }

    public ICommand BrowseProcessCommand { get; }

    public ICommand RemoveProcessCommand { get; }

    public ICommand AddWordCommand { get; }

    public ICommand RemoveWordCommand { get; }

    public ICommand BrowseSwitchSoundCommand { get; }

    public ICommand BrowseFixSoundCommand { get; }

    public ICommand PlaySwitchSoundCommand { get; }

    public ICommand PlayFixSoundCommand { get; }

    public ICommand AddTypoCommand { get; }

    public ICommand RemoveTypoCommand { get; }

    public ICommand OpenDataFolderCommand { get; }

    public ICommand OpenReadmeCommand { get; }

    public ICommand CheckUpdatesCommand { get; }

    //────────────────────────────── Общие ──────────────────────────────

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public bool AutoSwitch
    {
        get => _autoSwitch;
        set => SetProperty(ref _autoSwitch, value);
    }

    public bool EarlySwitchEnabled
    {
        get => _earlySwitchEnabled;
        set => SetProperty(ref _earlySwitchEnabled, value);
    }

    public int EarlySwitchMinLength
    {
        get => _earlySwitchMinLength;
        set => SetProperty(ref _earlySwitchMinLength, value);
    }

    public bool SingleShiftSwitch
    {
        get => _singleShiftSwitch;
        set => SetProperty(ref _singleShiftSwitch, value);
    }

    public bool DoubleShiftUndo
    {
        get => _doubleShiftUndo;
        set => SetProperty(ref _doubleShiftUndo, value);
    }

    /// <summary>Не исправлять раскладку в играх: полноэкранные игры, папки с играми, движки.</summary>
    public bool GameDetectionEnabled
    {
        get => _gameDetectionEnabled;
        set => SetProperty(ref _gameDetectionEnabled, value);
    }

    public double ConfidenceThreshold
    {
        get => _confidenceThreshold;
        set => SetProperty(ref _confidenceThreshold, value);
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetProperty(ref _startWithWindows, value);
    }

    public bool RunAsAdmin
    {
        get => _runAsAdmin;
        set => SetProperty(ref _runAsAdmin, value);
    }

    public bool SafeMode
    {
        get => _safeMode;
        set => SetProperty(ref _safeMode, value);
    }

    public string ThemeValue
    {
        get => _themeValue;
        set => SetProperty(ref _themeValue, value ?? _themeValue);
    }

    public string LanguageValue
    {
        get => _languageValue;
        set => SetProperty(ref _languageValue, value ?? _languageValue);
    }

    /// <summary>Сочетание клавиш, например "Ctrl+Shift+Space"; пусто — выключено.</summary>
    public string ManualConvertHotkey
    {
        get => _manualConvertHotkey;
        set => SetProperty(ref _manualConvertHotkey, value ?? string.Empty);
    }

    public bool PauseHotkeyEnabled
    {
        get => _pauseHotkeyEnabled;
        set => SetProperty(ref _pauseHotkeyEnabled, value);
    }

    //────────────────────────────── Опечатки ───────────────────────────

    public bool TyposEnabled
    {
        get => _typosEnabled;
        set => SetProperty(ref _typosEnabled, value);
    }

    public bool CapsFixEnabled
    {
        get => _capsFixEnabled;
        set => SetProperty(ref _capsFixEnabled, value);
    }

    public bool YoFixEnabled
    {
        get => _yoFixEnabled;
        set => SetProperty(ref _yoFixEnabled, value);
    }

    public string SelectedTypoLanguage
    {
        get => _selectedTypoLanguage;
        set
        {
            if (SetProperty(ref _selectedTypoLanguage, value ?? _selectedTypoLanguage))
            {
                TypoView.Refresh();
            }
        }
    }

    public string NewTypoWrong
    {
        get => _newTypoWrong;
        set => SetProperty(ref _newTypoWrong, value ?? string.Empty);
    }

    public string NewTypoRight
    {
        get => _newTypoRight;
        set => SetProperty(ref _newTypoRight, value ?? string.Empty);
    }

    public string? TypoError => _typoErrorKey is null ? null : Loc.T(_typoErrorKey);

    public string TyposFilesText => Loc.Format("typos.files", _paths.TyposRuFile, _paths.TyposEnFile);

    //────────────────────────────── Звуки ─────────────────────────────

    public bool SoundsEnabled
    {
        get => _soundsEnabled;
        set => SetProperty(ref _soundsEnabled, value);
    }

    public string SwitchSoundPath
    {
        get => _switchSoundPath;
        set => SetProperty(ref _switchSoundPath, value ?? string.Empty);
    }

    public string FixSoundPath
    {
        get => _fixSoundPath;
        set => SetProperty(ref _fixSoundPath, value ?? string.Empty);
    }

    public double SoundVolume
    {
        get => _soundVolume;
        set => SetProperty(ref _soundVolume, value);
    }

    //──────────────────────────── Исключения ──────────────────────────

    public string NewProcessName
    {
        get => _newProcessName;
        set => SetProperty(ref _newProcessName, value ?? string.Empty);
    }

    public ExceptionMode NewProcessMode
    {
        get => _newProcessMode;
        set => SetProperty(ref _newProcessMode, value);
    }

    public string? ProcessError => _processErrorKey is null ? null : Loc.T(_processErrorKey);

    public bool HasProcessError => _processErrorKey is not null;

    public string NewWord
    {
        get => _newWord;
        set => SetProperty(ref _newWord, value ?? string.Empty);
    }

    public string NewWordDirection
    {
        get => _newWordDirection;
        set => SetProperty(ref _newWordDirection, ExceptionStore.NormalizeDirection(value));
    }

    public string? WordError => _wordErrorKey is null ? null : Loc.T(_wordErrorKey);

    public bool HasWordError => _wordErrorKey is not null;

    //───────────────────────────── Состояние ───────────────────────────

    public string? StatusMessage => _statusKey is null ? null : Loc.Format(_statusKey, _statusArgs);

    public bool HasStatusMessage => _statusKey is not null;

    public string? ValidationMessage =>
        _validationKey is null ? null : Loc.Format(_validationKey, _validationArgs);

    public bool HasValidationError
    {
        get => _hasValidationError;
        private set => SetProperty(ref _hasValidationError, value);
    }

    public string VersionText
    {
        get => _versionText;
        private set => SetProperty(ref _versionText, value);
    }

    /// <summary>Строка «Версия …» для вкладки «О программе» (зависит от языка).</summary>
    public string AboutVersionText => Loc.Format("about.version", VersionText);

    /// <summary>Тексты лицензий из embedded-ресурсов либо локализованная заглушка.</summary>
    public string LicensesText => _licensesRaw ?? Loc.T("about.licensesFallback");

    public string LearningSummary =>
        Loc.Format("about.learningSummary", _learning.TotalUndos, _learning.TotalAccepted, _suppressedWords);

    public string DataFolderPath => _paths.Root;

    //─────────────────────────────── Логика ───────────────────────────

    /// <summary>Перечитать состояние из Data (после открытия окна или отмены правок).</summary>
    public void Reload()
    {
        Enabled = _settings.Enabled;
        AutoSwitch = _settings.AutoSwitch;
        EarlySwitchEnabled = _settings.EarlySwitchEnabled;
        EarlySwitchMinLength = _settings.EarlySwitchMinLength;
        SingleShiftSwitch = _settings.SingleShiftSwitch;
        DoubleShiftUndo = _settings.DoubleShiftUndo;
        GameDetectionEnabled = _settings.GameDetectionEnabled;
        ConfidenceThreshold = _settings.ConfidenceThreshold;
        StartWithWindows = _settings.StartWithWindows;
        RunAsAdmin = _settings.RunAsAdmin;
        SafeMode = _settings.SafeMode;
        ThemeValue = NormalizeChoice(_settings.Theme, Themes, "system");
        LanguageValue = NormalizeChoice(_settings.Language, Languages, "ru");
        ManualConvertHotkey = _settings.ManualConvertHotkey ?? string.Empty;
        PauseHotkeyEnabled = _settings.PauseHotkeyEnabled;

        TyposEnabled = _settings.TyposEnabled;
        CapsFixEnabled = _settings.CapsFixEnabled;
        YoFixEnabled = _settings.YoFixEnabled;

        SoundsEnabled = _settings.SoundsEnabled;
        SwitchSoundPath = _settings.SwitchSoundPath ?? string.Empty;
        FixSoundPath = _settings.FixSoundPath ?? string.Empty;
        SoundVolume = Math.Clamp(_settings.SoundVolume, 0.0, 1.0);

        Processes.Clear();
        foreach (ProcessRule rule in _exceptions.Processes)
        {
            Processes.Add(new ProcessRow(rule.ProcessName, rule.Mode));
        }

        Words.Clear();
        foreach (WordException word in _exceptions.Words)
        {
            Words.Add(new WordRow(word.Word, word.Direction, word.FromLearning, word.AddedAt));
        }

        LoadTypos();
        RefreshLearningSummary();

        NewProcessName = string.Empty;
        NewProcessMode = ExceptionMode.Full;
        NewWord = string.Empty;
        NewWordDirection = ExceptionStore.DirectionBoth;
        NewTypoWrong = string.Empty;
        NewTypoRight = string.Empty;
        ClearProcessError();
        ClearWordError();
        ClearTypoError();
        ClearValidation();
        ClearStatus();
    }

    /// <summary>Проверить и сохранить настройки. Возвращает false, если есть ошибки валидации.</summary>
    public bool Apply()
    {
        ClearValidation();

        if (!Validate())
        {
            return false;
        }

        CopyToSettings();

        // Язык применяем сразу: привязки к Loc.Instance перечитываются сами.
        Loc.Instance.SetLanguage(_settings.Language);

        try
        {
            ApplyExceptions();
            ApplyTypos();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error("Не удалось сохранить исключения или словарь опечаток.", ex);
            SetValidation("error.saveData", ex.Message);
            return false;
        }

        if (!string.IsNullOrEmpty(_typos.LastError))
        {
            SetValidation("error.typosSave", _typos.LastError);
            return false;
        }

        _logger.Info("Настройки сохранены из окна настроек.");
        SetStatus("status.saved");
        Saved?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private bool Validate()
    {
        if (EarlySwitchMinLength < 2 || EarlySwitchMinLength > 20)
        {
            SetValidation("error.earlyLength");
            return false;
        }

        if (ConfidenceThreshold < 0.5 || ConfidenceThreshold > 5.0)
        {
            SetValidation("error.confidence");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(ManualConvertHotkey) && !IsValidHotkey(ManualConvertHotkey))
        {
            SetValidation("error.hotkey");
            return false;
        }

        foreach (TypoRow row in TypoEntries)
        {
            string right = (row.Right ?? string.Empty).Trim();
            if (right.Length == 0 || right.Contains('='))
            {
                SelectedTypoLanguage = row.Language;
                SetValidation("error.typoReplacement", row.Wrong ?? string.Empty);
                return false;
            }
        }

        return true;
    }

    private static bool IsValidHotkey(string value)
    {
        string[] parts = value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        string last = parts[^1];
        return last.Length > 0 && !IsModifier(last);
    }

    private static bool IsModifier(string token) =>
        token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
        || token.Equals("Control", StringComparison.OrdinalIgnoreCase)
        || token.Equals("Shift", StringComparison.OrdinalIgnoreCase)
        || token.Equals("Alt", StringComparison.OrdinalIgnoreCase)
        || token.Equals("Win", StringComparison.OrdinalIgnoreCase);

    private void CopyToSettings()
    {
        _settings.Enabled = Enabled;
        _settings.AutoSwitch = AutoSwitch;
        _settings.EarlySwitchEnabled = EarlySwitchEnabled;
        _settings.EarlySwitchMinLength = Math.Clamp(EarlySwitchMinLength, 2, 20);
        _settings.SingleShiftSwitch = SingleShiftSwitch;
        _settings.DoubleShiftUndo = DoubleShiftUndo;
        _settings.GameDetectionEnabled = GameDetectionEnabled;
        _settings.ConfidenceThreshold = Math.Clamp(Math.Round(ConfidenceThreshold, 2), 0.5, 5.0);
        _settings.StartWithWindows = StartWithWindows;
        _settings.RunAsAdmin = RunAsAdmin;
        _settings.SafeMode = SafeMode;
        _settings.Theme = NormalizeChoice(ThemeValue, Themes, "system");
        _settings.Language = NormalizeChoice(LanguageValue, Languages, "system");
        _settings.ManualConvertHotkey = ManualConvertHotkey.Trim();
        _settings.PauseHotkeyEnabled = PauseHotkeyEnabled;

        _settings.TyposEnabled = TyposEnabled;
        _settings.CapsFixEnabled = CapsFixEnabled;
        _settings.YoFixEnabled = YoFixEnabled;

        _settings.SoundsEnabled = SoundsEnabled;
        _settings.SwitchSoundPath = SwitchSoundPath.Trim();
        _settings.FixSoundPath = FixSoundPath.Trim();
        _settings.SoundVolume = Math.Clamp(SoundVolume, 0.0, 1.0);
    }

    private void ApplyExceptions()
    {
        _exceptions.Processes.Clear();
        foreach (ProcessRow row in Processes)
        {
            _exceptions.Processes.Add(new ProcessRule(row.Name, row.Mode));
        }

        _exceptions.Words.Clear();
        foreach (WordRow row in Words)
        {
            _exceptions.Words.Add(new WordException
            {
                Word = row.Word,
                Direction = row.Direction,
                FromLearning = row.FromLearning,
                AddedAt = row.AddedAt,
            });
        }

        _exceptions.Save();
    }

    private void ApplyTypos()
    {
        ApplyTypoLanguage("ru", _typosRuSnapshot);
        ApplyTypoLanguage("en", _typosEnSnapshot);
        _typos.Save();
        SnapshotTypos();
    }

    private void ApplyTypoLanguage(string language, Dictionary<string, string> snapshot)
    {
        Dictionary<string, string> staged = TypoEntries
            .Where(row => row.Language == language)
            .GroupBy(row => row.Wrong, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Right, StringComparer.OrdinalIgnoreCase);

        foreach (string removed in snapshot.Keys.Where(key => !staged.ContainsKey(key)))
        {
            _typos.RemoveEntry(language, removed);
        }

        foreach (KeyValuePair<string, string> pair in staged)
        {
            _typos.SetEntry(language, pair.Key, pair.Value);
        }
    }

    //─────────────────────────── Процессы ──────────────────────────────

    private void AddProcess()
    {
        ClearProcessError();
        string name = NormalizeProcessInput(NewProcessName);

        if (name.Length == 0)
        {
            SetProcessError("error.processEmpty");
            return;
        }

        if (Processes.Any(row => row.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            SetProcessError("error.processDuplicate");
            return;
        }

        Processes.Add(new ProcessRow(name, NewProcessMode));
        NewProcessName = string.Empty;
        NewProcessMode = ExceptionMode.Full;
    }

    private static string NormalizeProcessInput(string? input)
    {
        string value = (input ?? string.Empty).Trim().Trim('"');
        if (value.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            string fileName = Path.GetFileName(value);
            if (!string.IsNullOrEmpty(fileName))
            {
                value = fileName;
            }
        }
        catch (ArgumentException)
        {
            // Оставляем как ввёл человек — проверка сработает по имени.
        }

        if (!value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            value += ".exe";
        }

        return value;
    }

    private void BrowseProcess()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("dialog.chooseApp"),
            Filter = Loc.T("filter.programs"),
        };

        if (dialog.ShowDialog() == true)
        {
            NewProcessName = Path.GetFileName(dialog.FileName);
            ClearProcessError();
        }
    }

    private void RemoveProcess(ProcessRow? row)
    {
        if (row is not null)
        {
            Processes.Remove(row);
        }
    }

    //───────────────────────── Слова-исключения ────────────────────────

    private void AddWord()
    {
        ClearWordError();
        string word = NewWord.Trim();

        if (!_exceptions.CanAddWordException(word, out string error))
        {
            SetWordError(MapWordError(error));
            return;
        }

        string normalized = word.ToLowerInvariant();
        string direction = ExceptionStore.NormalizeDirection(NewWordDirection);

        if (Words.Any(row => row.Word.Equals(normalized, StringComparison.OrdinalIgnoreCase)
                             && row.Direction == direction))
        {
            SetWordError("error.wordDuplicateDirection");
            return;
        }

        if (direction == ExceptionStore.DirectionBoth
            && Words.Any(row => row.Word.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            SetWordError("error.wordConflict");
            return;
        }

        Words.Add(new WordRow(normalized, direction));
        NewWord = string.Empty;
    }

    /// <summary>Переводит русские тексты ExceptionStore на ключи локализации.</summary>
    private static string MapWordError(string? error)
    {
        return error switch
        {
            null or "" => "error.wordGeneric",
            "Слово не задано." => "error.wordEmpty",
            "Числа и слова с цифрами не добавляются в исключения." => "error.wordDigits",
            "Слово должно состоять только из букв." => "error.wordLetters",
            "Поддерживаются только русские и английские буквы." => "error.wordAlphabet",
            "Слишком короткое слово: нужно минимум 2 буквы." => "error.wordTooShort",
            "Слово не должно смешивать русские и английские буквы." => "error.wordMixed",
            "Это слово входит в топ-100 частотных — его нельзя исключать." => "error.wordFrequent",
            _ => "error.wordGeneric",
        };
    }

    private void RemoveWord(WordRow? row)
    {
        if (row is not null)
        {
            Words.Remove(row);
        }
    }

    //─────────────────────────── Опечатки ──────────────────────────────

    private void LoadTypos()
    {
        TypoEntries.Clear();
        _typos.Reload();

        foreach (KeyValuePair<string, string> pair in _typos.RuEntries.OrderBy(pair => pair.Key))
        {
            TypoEntries.Add(new TypoRow("ru", pair.Key, pair.Value));
        }

        foreach (KeyValuePair<string, string> pair in _typos.EnEntries.OrderBy(pair => pair.Key))
        {
            TypoEntries.Add(new TypoRow("en", pair.Key, pair.Value));
        }

        SnapshotTypos();
        TypoView.Refresh();
    }

    private void SnapshotTypos()
    {
        _typosRuSnapshot.Clear();
        foreach (KeyValuePair<string, string> pair in _typos.RuEntries)
        {
            _typosRuSnapshot[pair.Key] = pair.Value;
        }

        _typosEnSnapshot.Clear();
        foreach (KeyValuePair<string, string> pair in _typos.EnEntries)
        {
            _typosEnSnapshot[pair.Key] = pair.Value;
        }
    }

    private void AddTypo()
    {
        ClearTypoError();
        string wrong = NewTypoWrong.Trim().ToLowerInvariant();
        string right = NewTypoRight.Trim();

        if (wrong.Length == 0 || right.Length == 0)
        {
            SetTypoError("error.typoBothFields");
            return;
        }

        if (wrong.Contains('=') || right.Contains('='))
        {
            SetTypoError("error.typoEquals");
            return;
        }

        if (wrong == right)
        {
            SetTypoError("error.typoSame");
            return;
        }

        TypoRow? existing = TypoEntries.FirstOrDefault(
            row => row.Language == SelectedTypoLanguage && row.Wrong.Equals(wrong, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.Right = right;
        }
        else
        {
            TypoEntries.Add(new TypoRow(SelectedTypoLanguage, wrong, right));
        }

        _typos.SetEntry(SelectedTypoLanguage, wrong, right);

        NewTypoWrong = string.Empty;
        NewTypoRight = string.Empty;
        TypoView.Refresh();
    }

    private void RemoveTypo(TypoRow? row)
    {
        if (row is null)
        {
            return;
        }

        TypoEntries.Remove(row);
        _typos.RemoveEntry(row.Language, row.Wrong);
        TypoView.Refresh();
    }

    //───────────────────────────── Звуки ───────────────────────────────

    private void BrowseSound(bool isSwitch)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T(isSwitch ? "dialog.switchSound" : "dialog.fixSound"),
            Filter = Loc.T("filter.sounds"),
        };

        if (dialog.ShowDialog() == true)
        {
            if (isSwitch)
            {
                SwitchSoundPath = dialog.FileName;
            }
            else
            {
                FixSoundPath = dialog.FileName;
            }

            SetStatus("status.soundPathChosen");
        }
    }

    private void PlaySound(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            SetStatus("status.chooseSoundFirst");
            return;
        }

        if (!File.Exists(path))
        {
            SetStatus("status.soundNotFound", path);
            return;
        }

        SoundService? sounds = SoundService.Shared;
        if (sounds is null)
        {
            SetStatus("status.previewUnavailable");
            return;
        }

        sounds.SetVolume(SoundVolume);
        sounds.Preview(path);
        SetStatus("status.playing");
    }

    //─────────────────────────── О программе ───────────────────────────

    private void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(_paths.Root);
            Process.Start(new ProcessStartInfo
            {
                FileName = _paths.Root,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.Warn("Не удалось открыть папку данных: " + ex.Message);
            SetStatus("status.dataFolderFailed", ex.Message);
        }
    }

    private void OpenReadme()
    {
        string? readme = FindReadme();
        if (readme is null)
        {
            SetStatus("status.readmeMissing");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = readme, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            SetStatus("status.readmeFailed", ex.Message);
        }
    }

    private static string? FindReadme()
    {
        string? directory = AppContext.BaseDirectory;

        for (int depth = 0; depth < 6 && !string.IsNullOrEmpty(directory); depth++)
        {
            string[] candidates =
            {
                Path.Combine(directory, "README.md"),
                Path.Combine(directory, "docs", "README.md"),
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        return null;
    }

    private static string ReadVersion()
    {
        Version? version = typeof(SettingsViewModel).Assembly.GetName().Version;
        return version is null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    /// <summary>Лицензии словарей: embedded-ресурсы KeySwitch.Data (Assets/licenses/*).</summary>
    private static string? LoadLicenses()
    {
        try
        {
            Assembly dataAssembly = typeof(AppSettings).Assembly;
            var builder = new StringBuilder();

            foreach (string name in dataAssembly.GetManifestResourceNames()
                         .Where(name => name.Contains("license", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(name => name))
            {
                using Stream? stream = dataAssembly.GetManifestResourceStream(name);
                if (stream is null)
                {
                    continue;
                }

                using var reader = new StreamReader(stream, Encoding.UTF8);
                string text = reader.ReadToEnd().Trim();
                if (text.Length == 0)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.AppendLine();
                    builder.AppendLine(new string('─', 40));
                    builder.AppendLine();
                }

                builder.AppendLine(name.Replace('_', '/'));
                builder.AppendLine();
                builder.Append(text);
            }

            return builder.Length == 0 ? null : builder.ToString().TrimEnd();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void RefreshLearningSummary()
    {
        _suppressedWords = _learning.Snapshot().Values.Count(stats => stats.Suppressed);
        OnPropertyChanged(nameof(LearningSummary));
    }

    private static string NormalizeChoice(string? value, IReadOnlyList<ChoiceOption> options, string fallback)
    {
        if (string.IsNullOrEmpty(value))
        {
            return fallback;
        }

        foreach (ChoiceOption option in options)
        {
            if (option.Value.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                return option.Value;
            }
        }

        return fallback;
    }

    //────────────────────────── Локализация ────────────────────────────

    /// <summary>
    /// Смена языка: Loc поднимает PropertyChanged с пустым именем. Обновляем
    /// подписи вариантов на месте (списки не пересоздаём, иначе ComboBox теряет
    /// выбранный пункт) и переформатируем тексты.
    /// </summary>
    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName))
        {
            return;
        }

        OptionLists.RefreshTitles(Themes);
        OptionLists.RefreshTitles(Languages);
        OptionLists.RefreshTitles(Directions);
        OptionLists.RefreshTitles(TypoLanguages);
        OptionLists.RefreshTitles(Modes);

        OnPropertyChanged(nameof(TyposFilesText));
        OnPropertyChanged(nameof(LearningSummary));
        OnPropertyChanged(nameof(AboutVersionText));
        OnPropertyChanged(nameof(LicensesText));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(ProcessError));
        OnPropertyChanged(nameof(WordError));
        OnPropertyChanged(nameof(TypoError));
        OnPropertyChanged(nameof(HasProcessError));
        OnPropertyChanged(nameof(HasWordError));
    }

    //────────────────────── Состояние и ошибки: ключи ──────────────────

    private void SetStatus(string key, params object?[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(HasStatusMessage));
    }

    private void ClearStatus()
    {
        _statusKey = null;
        _statusArgs = Array.Empty<object?>();
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(HasStatusMessage));
    }

    private void SetValidation(string key, params object?[] args)
    {
        _validationKey = key;
        _validationArgs = args;
        HasValidationError = true;
        OnPropertyChanged(nameof(ValidationMessage));
    }

    private void ClearValidation()
    {
        _validationKey = null;
        _validationArgs = Array.Empty<object?>();
        HasValidationError = false;
        OnPropertyChanged(nameof(ValidationMessage));
    }

    private void SetProcessError(string key)
    {
        _processErrorKey = key;
        OnPropertyChanged(nameof(ProcessError));
        OnPropertyChanged(nameof(HasProcessError));
    }

    private void ClearProcessError()
    {
        _processErrorKey = null;
        OnPropertyChanged(nameof(ProcessError));
        OnPropertyChanged(nameof(HasProcessError));
    }

    private void SetWordError(string key)
    {
        _wordErrorKey = key;
        OnPropertyChanged(nameof(WordError));
        OnPropertyChanged(nameof(HasWordError));
    }

    private void ClearWordError()
    {
        _wordErrorKey = null;
        OnPropertyChanged(nameof(WordError));
        OnPropertyChanged(nameof(HasWordError));
    }

    private void SetTypoError(string key)
    {
        _typoErrorKey = key;
        OnPropertyChanged(nameof(TypoError));
    }

    private void ClearTypoError()
    {
        _typoErrorKey = null;
        OnPropertyChanged(nameof(TypoError));
    }
}
