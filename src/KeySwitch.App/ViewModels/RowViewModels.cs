using KeySwitch.Data;

namespace KeySwitch.App.ViewModels;

/// <summary>Строка списка процессов (staging-копия, в ExceptionStore попадает при Apply).</summary>
public sealed class ProcessRow : ViewModelBase
{
    private ExceptionMode _mode;

    public ProcessRow(string name, ExceptionMode mode)
    {
        Name = name;
        _mode = mode;
    }

    public string Name { get; }

    public ExceptionMode Mode
    {
        get => _mode;
        set => SetProperty(ref _mode, value);
    }
}

/// <summary>Строка списка слов-исключений.</summary>
public sealed class WordRow : ViewModelBase
{
    private string _direction;

    public WordRow(string word, string direction, bool fromLearning = false, DateTime? addedAt = null)
    {
        Word = word;
        _direction = ExceptionStore.NormalizeDirection(direction);
        FromLearning = fromLearning;
        AddedAt = addedAt ?? DateTime.Now;
    }

    public string Word { get; }

    public string Direction
    {
        get => _direction;
        set => SetProperty(ref _direction, ExceptionStore.NormalizeDirection(value));
    }

    public bool FromLearning { get; }

    public DateTime AddedAt { get; }
}

/// <summary>Строка редактора словаря опечаток.</summary>
public sealed class TypoRow : ViewModelBase
{
    private string _right;

    public TypoRow(string language, string wrong, string right)
    {
        Language = language;
        Wrong = wrong;
        _right = right;
    }

    /// <summary>"ru" | "en".</summary>
    public string Language { get; }

    public string Wrong { get; }

    public string Right
    {
        get => _right;
        set => SetProperty(ref _right, value);
    }
}
