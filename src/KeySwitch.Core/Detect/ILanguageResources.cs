using KeySwitch.Core.Text;

namespace KeySwitch.Core.Detect;

/// <summary>Словарь слов языка (без морфологии — формы уже развёрнуты при сборке данных).</summary>
public interface IWordDictionary
{
    string Language { get; }

    int Count { get; }

    /// <summary>Проверка наличия слова без учёта регистра.</summary>
    bool Contains(string word);

    /// <summary>Частота слова (-1, если слова нет; 0 — слово есть, частота неизвестна).</summary>
    int GetFrequency(string word);
}

/// <summary>Символьная n-граммная модель языка (3- и 4-граммы, ключ — хеш n-граммы).</summary>
public interface ICharNgramModel
{
    string Language { get; }

    int Order { get; }

    int GramCount { get; }

    /// <summary>log10 вероятности n-граммы с учётом сглаживания.</summary>
    double GetLogProbHash(ulong hash);

    bool HasGramHash(ulong hash);

    double GetLogProb(ReadOnlySpan<char> gram) => GetLogProbHash(NgramHasher.Hash(gram));

    bool HasGram(ReadOnlySpan<char> gram) => HasGramHash(NgramHasher.Hash(gram));
}
