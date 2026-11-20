namespace KeySwitch.Core.Learning;

/// <summary>Вердикт по слову-исключению.</summary>
public enum WordVerdict
{
    Allow,
    BlockedByUser,
    BlockedByLearning,
    TooShort,
    TooFrequent,
    NotAWord
}

/// <summary>Пользовательские исключения и правила «не трогать это слово».</summary>
public interface IExceptionPolicy
{
    WordVerdict CheckWord(string word, string sourceLayoutId, string targetLayoutId);

    /// <summary>Можно ли добавлять слово в исключения (запрещены одиночные буквы и частотные слова).</summary>
    bool CanAddWordException(string word, out string error);
}

/// <summary>
/// Самообучение по отдельным словам и подтверждённым заменам раскладки.
/// </summary>
public interface ILearningStore
{
    int GetUndoCount(string word, string sourceLayoutId, string targetLayoutId);

    bool IsSuppressed(string word, string sourceLayoutId, string targetLayoutId);

    void RecordUndo(string word, string sourceLayoutId, string targetLayoutId, bool keepOriginal = false);

    bool TryGetManualCorrection(string word, string sourceLayoutId, string targetLayoutId, out string replacement);

    bool RecordManualCorrection(string original, string corrected, string sourceLayoutId, string targetLayoutId);

    void RecordAccepted(string word, string sourceLayoutId, string targetLayoutId);
}

/// <summary>Пустая реализация для сценариев без обучения (тесты, консольные инструменты).</summary>
public sealed class NullLearningStore : ILearningStore
{
    public static readonly NullLearningStore Instance = new();

    public int GetUndoCount(string word, string sourceLayoutId, string targetLayoutId) => 0;

    public bool IsSuppressed(string word, string sourceLayoutId, string targetLayoutId) => false;

    public void RecordUndo(string word, string sourceLayoutId, string targetLayoutId, bool keepOriginal = false)
    {
    }

    public bool TryGetManualCorrection(string word, string sourceLayoutId, string targetLayoutId, out string replacement)
    {
        replacement = string.Empty;
        return false;
    }

    public bool RecordManualCorrection(string original, string corrected, string sourceLayoutId, string targetLayoutId) => false;

    public void RecordAccepted(string word, string sourceLayoutId, string targetLayoutId)
    {
    }
}

/// <summary>Политика без исключений — используется в тестах и в консольном стенде.</summary>
public sealed class NullExceptionPolicy : IExceptionPolicy
{
    public static readonly NullExceptionPolicy Instance = new();

    public WordVerdict CheckWord(string word, string sourceLayoutId, string targetLayoutId) => WordVerdict.Allow;

    public bool CanAddWordException(string word, out string error)
    {
        error = string.Empty;
        return true;
    }
}
