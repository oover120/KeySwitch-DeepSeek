namespace KeySwitch.Core.Layout;

/// <summary>
/// Реестр раскладок. Чтобы добавить третий язык, достаточно создать таблицу
/// (см. <see cref="LayoutTable.Create"/>) и зарегистрировать пару в <see cref="LayoutPairRegistry"/> —
/// ядро (детектор, буфер, исправление) при этом не меняется.
/// </summary>
public static class LayoutCatalog
{
    public const string EnUsId = "en-US";
    public const string RuRuId = "ru-RU";

    private static readonly Dictionary<string, LayoutTable> Tables = new(StringComparer.OrdinalIgnoreCase);

    static LayoutCatalog()
    {
        Register(BuildEnUs());
        Register(BuildRuRu());
    }

    public static LayoutTable EnUs => Tables[EnUsId];

    public static LayoutTable RuRu => Tables[RuRuId];

    public static IReadOnlyCollection<LayoutTable> All => Tables.Values;

    public static void Register(LayoutTable table) => Tables[table.Id] = table;

    public static LayoutTable? TryGet(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        if (Tables.TryGetValue(id, out var exact))
            return exact;
        // Допускаем короткие формы: "ru", "en", "RU".
        foreach (var table in Tables.Values)
        {
            if (string.Equals(table.Language, id, StringComparison.OrdinalIgnoreCase))
                return table;
        }
        return null;
    }

    private static LayoutTable BuildEnUs() => LayoutTable.Create(EnUsId, "English (US)", "en", new[]
    {
        new KeyDef(0x30, '1', '!'),
        new KeyDef(0x31, '2', '@'),
        new KeyDef(0x32, '3', '#'),
        new KeyDef(0x33, '4', '$'),
        new KeyDef(0x34, '5', '%'),
        new KeyDef(0x35, '6', '^'),
        new KeyDef(0x36, '7', '&'),
        new KeyDef(0x37, '8', '*'),
        new KeyDef(0x38, '9', '('),
        new KeyDef(0x39, '0', ')'),
        new KeyDef(0x41, 'a', 'A'),
        new KeyDef(0x42, 'b', 'B'),
        new KeyDef(0x43, 'c', 'C'),
        new KeyDef(0x44, 'd', 'D'),
        new KeyDef(0x45, 'e', 'E'),
        new KeyDef(0x46, 'f', 'F'),
        new KeyDef(0x47, 'g', 'G'),
        new KeyDef(0x48, 'h', 'H'),
        new KeyDef(0x49, 'i', 'I'),
        new KeyDef(0x4A, 'j', 'J'),
        new KeyDef(0x4B, 'k', 'K'),
        new KeyDef(0x4C, 'l', 'L'),
        new KeyDef(0x4D, 'm', 'M'),
        new KeyDef(0x4E, 'n', 'N'),
        new KeyDef(0x4F, 'o', 'O'),
        new KeyDef(0x50, 'p', 'P'),
        new KeyDef(0x51, 'q', 'Q'),
        new KeyDef(0x52, 'r', 'R'),
        new KeyDef(0x53, 's', 'S'),
        new KeyDef(0x54, 't', 'T'),
        new KeyDef(0x55, 'u', 'U'),
        new KeyDef(0x56, 'v', 'V'),
        new KeyDef(0x57, 'w', 'W'),
        new KeyDef(0x58, 'x', 'X'),
        new KeyDef(0x59, 'y', 'Y'),
        new KeyDef(0x5A, 'z', 'Z'),
        new KeyDef(0xBA, ';', ':'),
        new KeyDef(0xBB, '=', '+'),
        new KeyDef(0xBC, ',', '<'),
        new KeyDef(0xBD, '-', '_'),
        new KeyDef(0xBE, '.', '>'),
        new KeyDef(0xBF, '/', '?'),
        new KeyDef(0xC0, '`', '~'),
        new KeyDef(0xDB, '[', '{'),
        new KeyDef(0xDC, '\\', '|'),
        new KeyDef(0xDD, ']', '}'),
        new KeyDef(0xDE, '\'', '"'),
        new KeyDef(0x20, ' ', ' ')
    });

    /// <summary>
    /// Русская раскладка ЙЦУКЕН (Windows). Shift+4 даёт ';', Shift+3 — '№',
    /// Shift+7 — '?', Shift+6 — ':', Shift+5 — '%', Shift+8 — '*'.
    /// </summary>
    private static LayoutTable BuildRuRu() => LayoutTable.Create(RuRuId, "Русская (ЙЦУКЕН)", "ru", new[]
    {
        new KeyDef(0x30, '1', '!'),
        new KeyDef(0x31, '2', '"'),
        new KeyDef(0x32, '3', '№'),
        new KeyDef(0x33, '4', ';'),
        new KeyDef(0x34, '5', '%'),
        new KeyDef(0x35, '6', ':'),
        new KeyDef(0x36, '7', '?'),
        new KeyDef(0x37, '8', '*'),
        new KeyDef(0x38, '9', '('),
        new KeyDef(0x39, '0', ')'),
        new KeyDef(0x51, 'й', 'Й'),
        new KeyDef(0x57, 'ц', 'Ц'),
        new KeyDef(0x45, 'у', 'У'),
        new KeyDef(0x52, 'к', 'К'),
        new KeyDef(0x54, 'е', 'Е'),
        new KeyDef(0x59, 'н', 'Н'),
        new KeyDef(0x55, 'г', 'Г'),
        new KeyDef(0x49, 'ш', 'Ш'),
        new KeyDef(0x4F, 'щ', 'Щ'),
        new KeyDef(0x50, 'з', 'З'),
        new KeyDef(0xDB, 'х', 'Х'),
        new KeyDef(0xDD, 'ъ', 'Ъ'),
        new KeyDef(0x41, 'ф', 'Ф'),
        new KeyDef(0x53, 'ы', 'Ы'),
        new KeyDef(0x44, 'в', 'В'),
        new KeyDef(0x46, 'а', 'А'),
        new KeyDef(0x47, 'п', 'П'),
        new KeyDef(0x48, 'р', 'Р'),
        new KeyDef(0x4A, 'о', 'О'),
        new KeyDef(0x4B, 'л', 'Л'),
        new KeyDef(0x4C, 'д', 'Д'),
        new KeyDef(0xBA, 'ж', 'Ж'),
        new KeyDef(0xDE, 'э', 'Э'),
        new KeyDef(0x5A, 'я', 'Я'),
        new KeyDef(0x58, 'ч', 'Ч'),
        new KeyDef(0x43, 'с', 'С'),
        new KeyDef(0x56, 'м', 'М'),
        new KeyDef(0x42, 'и', 'И'),
        new KeyDef(0x4E, 'т', 'Т'),
        new KeyDef(0x4D, 'ь', 'Ь'),
        new KeyDef(0xBC, 'б', 'Б'),
        new KeyDef(0xBE, 'ю', 'Ю'),
        new KeyDef(0xC0, 'ё', 'Ё'),
        new KeyDef(0xBF, '.', ','),
        new KeyDef(0xBD, '-', '_'),
        new KeyDef(0xBB, '=', '+'),
        new KeyDef(0xDC, '\\', '/'),
        new KeyDef(0x20, ' ', ' ')
    });
}
