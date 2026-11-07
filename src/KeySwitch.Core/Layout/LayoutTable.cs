namespace KeySwitch.Core.Layout;

/// <summary>Описание одной физической клавиши в конкретной раскладке.</summary>
public readonly record struct KeyDef(int VirtualKey, char Unshifted, char? Shifted);

/// <summary>
/// Таблица раскладки: соответствие «физическая клавиша + Shift → символ».
/// Все преобразования текста между языками строятся только по физическим клавишам,
/// поэтому таблица является единственным источником истины для конвертации.
/// </summary>
public sealed class LayoutTable
{
    private readonly Dictionary<int, KeyDef> _byVk = new();
    private readonly Dictionary<char, (int Vk, bool Shift)> _byChar = new();

    private LayoutTable(string id, string displayName, string language)
    {
        Id = id;
        DisplayName = displayName;
        Language = language;
    }

    /// <summary>Идентификатор раскладки, совпадает с HKL-именем Windows, например "ru-RU".</summary>
    public string Id { get; }

    public string DisplayName { get; }

    /// <summary>Двухбуквенный код языка: "ru", "en".</summary>
    public string Language { get; }

    public IReadOnlyCollection<char> Characters => _byChar.Keys;

    public IReadOnlyCollection<KeyDef> Keys => _byVk.Values;

    public static LayoutTable Create(string id, string displayName, string language, IEnumerable<KeyDef> defs)
    {
        var table = new LayoutTable(id, displayName, language);
        foreach (var def in defs)
        {
            table._byVk[def.VirtualKey] = def;
            // Первым регистрируется «нижний» символ: он имеет приоритет при обратном поиске.
            if (!table._byChar.ContainsKey(def.Unshifted))
                table._byChar[def.Unshifted] = (def.VirtualKey, false);
            if (def.Shifted is { } shifted && !table._byChar.ContainsKey(shifted))
                table._byChar[shifted] = (def.VirtualKey, true);
        }

        return table;
    }

    public bool TryMap(int virtualKey, bool shift, out char character)
    {
        character = default;
        if (!_byVk.TryGetValue(virtualKey, out var def))
            return false;

        if (shift)
        {
            if (def.Shifted is not { } shifted)
                return false;
            character = shifted;
            return true;
        }

        character = def.Unshifted;
        return true;
    }

    public bool TryGetKey(char character, out int virtualKey, out bool shift)
    {
        if (_byChar.TryGetValue(character, out var key))
        {
            virtualKey = key.Vk;
            shift = key.Shift;
            return true;
        }

        virtualKey = 0;
        shift = false;
        return false;
    }

    public bool Contains(char character) => _byChar.ContainsKey(character);

    public override string ToString() => Id;
}
