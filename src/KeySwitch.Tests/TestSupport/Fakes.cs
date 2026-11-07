using KeySwitch.Core.Learning;
using KeySwitch.Core.PostProcess;

namespace KeySwitch.Tests.TestSupport;

/// <summary>Фейковое хранилище обучения: считает вызовы и умеет подавлять слова.</summary>
public sealed class FakeLearningStore : ILearningStore
{
    public List<(string Word, string Source, string Target)> Undos { get; } = new();

    public List<(string Original, string Corrected, string Source, string Target)> ManualCorrections { get; } = new();

    public List<(string Word, string Source, string Target)> Accepted { get; } = new();

    public HashSet<string> Suppressed { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<(string Word, string Source, string Target), int> UndoCounts { get; } = new();

    public int GetUndoCount(string word, string sourceLayoutId, string targetLayoutId)
        => UndoCounts.GetValueOrDefault((word, sourceLayoutId, targetLayoutId));

    public bool IsSuppressed(string word, string sourceLayoutId, string targetLayoutId)
        => Suppressed.Contains(word)
           || GetUndoCount(word, sourceLayoutId, targetLayoutId) >= 3;

    public void RecordUndo(string word, string sourceLayoutId, string targetLayoutId)
    {
        Undos.Add((word, sourceLayoutId, targetLayoutId));
        var key = (word, sourceLayoutId, targetLayoutId);
        UndoCounts[key] = GetUndoCount(word, sourceLayoutId, targetLayoutId) + 1;
    }

    public void RecordManualCorrection(string original, string corrected, string sourceLayoutId, string targetLayoutId)
        => ManualCorrections.Add((original, corrected, sourceLayoutId, targetLayoutId));

    public void RecordAccepted(string word, string sourceLayoutId, string targetLayoutId)
        => Accepted.Add((word, sourceLayoutId, targetLayoutId));
}

/// <summary>Политика исключений с настраиваемым вердиктом и правилом добавления слова.</summary>
public sealed class FakeExceptionPolicy : IExceptionPolicy
{
    public WordVerdict Verdict { get; set; } = WordVerdict.Allow;

    public bool CanAdd { get; set; } = true;

    public string AddError { get; set; } = "слово нельзя добавить";

    public List<string> CheckedWords { get; } = new();

    public WordVerdict CheckWord(string word, string sourceLayoutId, string targetLayoutId)
    {
        CheckedWords.Add(word);
        return Verdict;
    }

    public bool CanAddWordException(string word, out string error)
    {
        error = CanAdd ? string.Empty : AddError;
        return CanAdd;
    }
}

/// <summary>Словарь опечаток в памяти.</summary>
public sealed class FakeTypoDictionary : ITypoDictionary
{
    public Dictionary<string, string> Map { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int Count => Map.Count;

    public bool TryGetReplacement(string word, out string replacement)
    {
        if (Map.TryGetValue(word, out var value))
        {
            replacement = value;
            return true;
        }

        replacement = string.Empty;
        return false;
    }
}

/// <summary>Словарь «ё»-замен в памяти.</summary>
public sealed class FakeYoDictionary : IYoDictionary
{
    public Dictionary<string, string> Map { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int Count => Map.Count;

    public bool TryGetYoWord(string word, out string yoWord)
    {
        if (Map.TryGetValue(word, out var value))
        {
            yoWord = value;
            return true;
        }

        yoWord = string.Empty;
        return false;
    }
}
