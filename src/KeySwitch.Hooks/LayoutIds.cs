using System.Globalization;

namespace KeySwitch.Hooks;

/// <summary>
/// Преобразование между HKL/LCID Windows и строковыми идентификаторами раскладок,
/// которые использует ядро (<c>"en-US"</c>, <c>"ru-RU"</c>) и всё остальное приложение.
/// Неизвестные раскладки представляются как <c>"0xXXXX"</c> (младшие 16 бит HKL).
/// </summary>
internal static class LayoutIds
{
    internal const int EnUs = 0x0409;
    internal const int RuRu = 0x0419;

    /// <summary>Младшие 16 бит HKL — это идентификатор языка (LCID).</summary>
    internal static int LangIdFromHkl(IntPtr hkl)
    {
        if (hkl == IntPtr.Zero)
            return 0;

        return unchecked((int)(hkl.ToInt64() & 0xFFFF));
    }

    internal static string FromHkl(IntPtr hkl) => ToId(LangIdFromHkl(hkl));

    internal static string ToId(int langId) => langId switch
    {
        EnUs => "en-US",
        RuRu => "ru-RU",
        _ => "0x" + langId.ToString("X4", CultureInfo.InvariantCulture)
    };

    /// <summary>
    /// Разбирает строковый идентификатор раскладки: <c>"en-US"</c>, <c>"ru"</c>,
    /// <c>"0x0409"</c>, <c>"0409"</c> или KLID вида <c>"00000409"</c>.
    /// </summary>
    internal static bool TryParse(string? layoutId, out int langId)
    {
        langId = 0;
        if (string.IsNullOrWhiteSpace(layoutId))
            return false;

        string value = layoutId.Trim();
        if (value.Equals("en-US", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("en", StringComparison.OrdinalIgnoreCase))
        {
            langId = EnUs;
            return true;
        }

        if (value.Equals("ru-RU", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("ru", StringComparison.OrdinalIgnoreCase))
        {
            langId = RuRu;
            return true;
        }

        string hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        if (hex.Length is 4 or 8 &&
            int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int parsed))
        {
            // Для KLID ("00000419") значим только младший WORD — там лежит LCID.
            langId = parsed & 0xFFFF;
            return langId != 0;
        }

        return false;
    }

    internal static string ToKlid(int langId) => langId.ToString("X8", CultureInfo.InvariantCulture);
}
