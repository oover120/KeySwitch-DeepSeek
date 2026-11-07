using KeySwitch.Core.Text;

namespace KeySwitch.Core.PostProcess;

/// <summary>
/// Постобработка слова, когда раскладка верна. Каждый обработчик независим и включается
/// отдельной опцией, поэтому Pro-функции не влияют друг на друга.
/// </summary>
public interface IPostProcessor
{
    string Id { get; }

    string Name { get; }

    /// <summary>Возвращает true, если слово нужно заменить.</summary>
    bool TryProcess(string word, out string result);
}

/// <summary>Словарь опечаток: «тоесть» → «то есть», «teh» → «the».</summary>
public interface ITypoDictionary
{
    bool TryGetReplacement(string word, out string replacement);

    int Count { get; }
}

/// <summary>Словарь однозначных «ё»-слов: «зеленый» → «зелёный».</summary>
public interface IYoDictionary
{
    bool TryGetYoWord(string word, out string yoWord);

    int Count { get; }
}

/// <summary>
/// «ПРивет» → «Привет»: две заглавные в начале и строчные дальше.
/// Полностью заглавные слова (аббревиатуры, крик) не трогаем.
/// </summary>
public sealed class CapsFixer : IPostProcessor
{
    public string Id => "caps";

    public string Name => "ИСправление";

    public bool TryProcess(string word, out string result)
    {
        result = word;
        if (word.Length < 3)
            return false;

        if (!char.IsUpper(word[0]) || !char.IsUpper(word[1]))
            return false;

        for (var i = 2; i < word.Length; i++)
        {
            if (!char.IsLower(word[i]))
                return false;
        }

        result = WordText.CapitalizeFirst(word);
        return !string.Equals(result, word, StringComparison.Ordinal);
    }
}

/// <summary>Опечатки по редактируемому словарю замен.</summary>
public sealed class TypoCorrector : IPostProcessor
{
    private readonly ITypoDictionary _dictionary;

    public TypoCorrector(ITypoDictionary dictionary) => _dictionary = dictionary;

    public string Id => "typo";

    public string Name => "Опечатки";

    public bool TryProcess(string word, out string result)
    {
        result = word;
        if (word.Length < 2)
            return false;

        var lower = word.ToLowerInvariant();
        if (!_dictionary.TryGetReplacement(lower, out var replacement) || replacement.Length == 0)
            return false;

        // Сохраняем регистр первой буквы исходного слова.
        if (char.IsUpper(word[0]) && replacement.Length > 0)
            replacement = char.ToUpperInvariant(replacement[0]) + replacement[1..];

        result = replacement;
        return !string.Equals(result, word, StringComparison.Ordinal);
    }
}

/// <summary>Расстановка «ё» только там, где замена однозначна по словарю.</summary>
public sealed class YoFixer : IPostProcessor
{
    private readonly IYoDictionary _dictionary;

    public YoFixer(IYoDictionary dictionary) => _dictionary = dictionary;

    public string Id => "yo";

    public string Name => "Ёфикатор";

    public bool TryProcess(string word, out string result)
    {
        result = word;
        if (word.Length < 2)
            return false;

        if (word.Contains('ё') || word.Contains('Ё'))
            return false;

        if (!word.Contains('е') && !word.Contains('Е'))
            return false;

        if (!_dictionary.TryGetYoWord(word.ToLowerInvariant(), out var yoWord) || yoWord.Length != word.Length)
            return false;

        result = yoWord;
        return !string.Equals(result, word, StringComparison.Ordinal);
    }
}

/// <summary>Цепочка постобработчиков: срабатывает первый, который дал результат.</summary>
public sealed class PostProcessorPipeline
{
    private readonly List<IPostProcessor> _processors = new();

    public PostProcessorPipeline(IEnumerable<IPostProcessor> processors) => _processors.AddRange(processors);

    public IReadOnlyList<IPostProcessor> Processors => _processors;

    public bool IsEmpty => _processors.Count == 0;

    public bool TryProcess(string word, out string result, out string processorId)
    {
        foreach (var processor in _processors)
        {
            if (processor.TryProcess(word, out result))
            {
                processorId = processor.Id;
                return true;
            }
        }

        result = word;
        processorId = string.Empty;
        return false;
    }
}
