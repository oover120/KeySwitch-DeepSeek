using KeySwitch.Core.Detect;
using KeySwitch.Core.Text;

namespace KeySwitch.Core.PostProcess;

/// <summary>Восстанавливает одну пропущенную букву внутри слова после смены раскладки.</summary>
public sealed class MissingLetterCorrector : IPostProcessor
{
    private const string RussianAlphabet = "абвгдеёжзийклмнопрстуфхцчшщъыьэюя";
    private const string EnglishAlphabet = "abcdefghijklmnopqrstuvwxyz";
    private const int MinimumFrequency = 1_000;
    private const int MinimumFrequencyRatio = 8;
    private const double MinimumQuadgramAdvantage = 0.4;

    private readonly ILanguageResourcesProvider _resources;

    public MissingLetterCorrector(ILanguageResourcesProvider resources) => _resources = resources;

    public string Id => "typo";

    public string Name => "Пропущенная буква";

    public bool TryProcess(string word, out string result)
    {
        result = word;
        if (word.Length is < 5 or > 24 || word.Skip(1).Any(char.IsUpper))
            return false;

        var language = word.All(WordText.IsCyrillicLetter) ? "ru" : word.All(WordText.IsLatinLetter) ? "en" : null;
        if (language == null)
            return false;

        var dictionary = _resources.GetDictionary(language);
        var quadgrams = _resources.GetQuadgrams(language);
        var normalized = WordText.Normalize(word);
        if (dictionary == null || quadgrams is not { Order: 4 } || dictionary.Contains(normalized))
            return false;

        var original = ScoreQuadgrams(normalized, quadgrams);
        if (!original.HasMissing)
            return false;

        var alphabet = language == "ru" ? RussianAlphabet : EnglishAlphabet;
        string? best = null;
        var bestFrequency = 0;
        var nextFrequency = 0;
        Span<char> candidateBuffer = stackalloc char[normalized.Length + 1];
        for (var position = 1; position < normalized.Length; position++)
        {
            normalized.AsSpan(0, position).CopyTo(candidateBuffer);
            normalized.AsSpan(position).CopyTo(candidateBuffer[(position + 1)..]);
            foreach (var letter in alphabet)
            {
                // Вставка с любой стороны одинаковых букв даёт одного кандидата.
                if (position > 1 && letter == normalized[position - 1])
                    continue;

                candidateBuffer[position] = letter;
                var candidate = new string(candidateBuffer);
                var frequency = dictionary.GetFrequency(candidate);
                if (frequency > bestFrequency)
                {
                    nextFrequency = bestFrequency;
                    bestFrequency = frequency;
                    best = candidate;
                }
                else
                {
                    nextFrequency = Math.Max(nextFrequency, frequency);
                }
            }
        }

        if (best == null || bestFrequency < MinimumFrequency || bestFrequency < (long)nextFrequency * MinimumFrequencyRatio ||
            ScoreQuadgrams(best, quadgrams).Mean - original.Mean < MinimumQuadgramAdvantage)
        {
            return false;
        }

        result = char.IsUpper(word[0]) ? WordText.CapitalizeFirst(best) : best;
        return true;
    }

    private static (double Mean, bool HasMissing) ScoreQuadgrams(string word, ICharNgramModel model)
    {
        double total = 0;
        var hasMissing = false;
        NgramHasher.ForEachGram(word, model.Order, hash =>
        {
            total += model.GetLogProbHash(hash);
            hasMissing |= !model.HasGramHash(hash);
        });
        return (total / (word.Length - 1), hasMissing);
    }
}
