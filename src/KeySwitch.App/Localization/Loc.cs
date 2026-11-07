using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;

namespace KeySwitch.App.Localization;

/// <summary>Язык интерфейса: как в системе, русский или английский.</summary>
public enum UiLanguage
{
    System,
    Russian,
    English,
}

/// <summary>
/// Строки интерфейса. Один экземпляр на процесс: XAML биндится к индексатору
/// (<c>{Binding [key], Source={x:Static loc:Loc.Instance}}</c>), код — к <see cref="T"/>.
/// Смена языка поднимает <see cref="PropertyChanged"/> с пустым именем, и все привязки
/// перечитывают значения без пересоздания окон.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    private static readonly Dictionary<string, string> Russian = Merge(UiStrings.Ru, RuntimeStrings.Ru);
    private static readonly Dictionary<string, string> English = Merge(UiStrings.En, RuntimeStrings.En);

    private UiLanguage _current = UiLanguage.System;
    private Dictionary<string, string> _active = Russian;

    private Loc()
    {
    }

    public static Loc Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Текущий действующий язык (после разрешения «как в системе»).</summary>
    public UiLanguage Current => _current;

    /// <summary>Двухбуквенный код действующего языка: "ru" или "en".</summary>
    public string Culture => _current == UiLanguage.English ? "en" : "ru";

    public string this[string key] => Lookup(key);

    /// <summary>Строка по ключу для кода: английский, затем русский, затем сам ключ.</summary>
    public static string T(string key) => Instance.Lookup(key);

    /// <summary>Строка по ключу с подстановкой аргументов (<c>string.Format</c>).</summary>
    public static string Format(string key, params object?[] args)
    {
        string text = Instance.Lookup(key);
        try
        {
            return string.Format(CultureInfo.InvariantCulture, text, args);
        }
        catch (FormatException)
        {
            return text;
        }
    }

    /// <summary>
    /// Применяет настройку языка ("ru", "en", "system" или пусто).
    /// Возвращает true, если действующий язык изменился.
    /// </summary>
    public bool SetLanguage(string? setting)
    {
        UiLanguage requested = Parse(setting);
        UiLanguage resolved = requested == UiLanguage.System ? SystemLanguage() : requested;
        bool changed = resolved != _current || _active.Count == 0;
        _current = resolved;
        _active = resolved == UiLanguage.English ? English : Russian;
        if (changed)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }

        return changed;
    }

    /// <summary>Разбирает значение настройки; неизвестное значение — «как в системе».</summary>
    public static UiLanguage Parse(string? setting) => setting?.Trim().ToLowerInvariant() switch
    {
        "ru" or "russian" => UiLanguage.Russian,
        "en" or "english" => UiLanguage.English,
        _ => UiLanguage.System,
    };

    /// <summary>Язык системы: русский для ru/uk/be, иначе английский.</summary>
    private static UiLanguage SystemLanguage()
    {
        string name = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return name is "ru" or "uk" or "be" or "kk" or "ky" ? UiLanguage.Russian : UiLanguage.English;
    }

    private string Lookup(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        if (_active.TryGetValue(key, out string? value) && value.Length > 0)
        {
            return value;
        }

        if (Russian.TryGetValue(key, out string? fallback) && fallback.Length > 0)
        {
            return fallback;
        }

        return key;
    }

    private static Dictionary<string, string> Merge(
        Dictionary<string, string> first,
        Dictionary<string, string> second)
    {
        var merged = new Dictionary<string, string>(first.Count + second.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> pair in first)
        {
            merged[pair.Key] = pair.Value;
        }

        foreach (KeyValuePair<string, string> pair in second)
        {
            merged[pair.Key] = pair.Value;
        }

        return merged;
    }
}
