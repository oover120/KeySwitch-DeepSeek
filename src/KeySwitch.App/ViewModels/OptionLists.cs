using System.ComponentModel;
using KeySwitch.App.Localization;
using KeySwitch.Data;

namespace KeySwitch.App.ViewModels;

/// <summary>
/// Вариант для ComboBox: идентификатор для настроек + ключ подписи.
/// Подпись берётся из <see cref="Loc"/> в момент чтения. Экземпляры живут всё
/// время работы окна, а при смене языка подпись обновляется через
/// <see cref="RefreshTitle"/> — так ComboBox сохраняет выбранный пункт.
/// </summary>
public sealed class ChoiceOption : INotifyPropertyChanged
{
    public ChoiceOption(string value, string titleKey)
    {
        Value = value;
        TitleKey = titleKey;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Value { get; }

    /// <summary>Ключ строки в <see cref="Loc"/>, например "options.theme.system".</summary>
    public string TitleKey { get; }

    public string Title => Loc.T(TitleKey);

    public override string ToString() => Title;

    /// <summary>Сообщает привязкам, что подпись изменилась после смены языка.</summary>
    public void RefreshTitle() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
}

/// <summary>Режим обработки процесса для ComboBox.</summary>
public sealed class ModeOption : INotifyPropertyChanged
{
    public ModeOption(ExceptionMode value, string titleKey)
    {
        Value = value;
        TitleKey = titleKey;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ExceptionMode Value { get; }

    /// <summary>Ключ строки в <see cref="Loc"/>, например "options.mode.full".</summary>
    public string TitleKey { get; }

    public string Title => Loc.T(TitleKey);

    public override string ToString() => Title;

    /// <summary>Сообщает привязкам, что подпись изменилась после смены языка.</summary>
    public void RefreshTitle() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
}

/// <summary>
/// Фабрики списков вариантов для вкладок настроек. ViewModel создаёт списки один
/// раз, а при смене языка обновляет подписи существующих экземпляров.
/// </summary>
public static class OptionLists
{
    public static IReadOnlyList<ChoiceOption> CreateThemes() => new[]
    {
        new ChoiceOption("system", "options.theme.system"),
        new ChoiceOption("light", "options.theme.light"),
        new ChoiceOption("dark", "options.theme.dark"),
    };

    public static IReadOnlyList<ChoiceOption> CreateLanguages() => new[]
    {
        new ChoiceOption("ru", "options.language.ru"),
        new ChoiceOption("en", "options.language.en"),
        new ChoiceOption("system", "options.language.system"),
    };

    public static IReadOnlyList<ChoiceOption> CreateDirections() => new[]
    {
        new ChoiceOption(ExceptionStore.DirectionBoth, "options.direction.both"),
        new ChoiceOption(ExceptionStore.DirectionRuEn, "options.direction.ruEn"),
        new ChoiceOption(ExceptionStore.DirectionEnRu, "options.direction.enRu"),
    };

    public static IReadOnlyList<ChoiceOption> CreateTypoLanguages() => new[]
    {
        new ChoiceOption("ru", "options.typo.ru"),
        new ChoiceOption("en", "options.typo.en"),
    };

    public static IReadOnlyList<ModeOption> CreateModes() => new[]
    {
        new ModeOption(ExceptionMode.Full, "options.mode.full"),
        new ModeOption(ExceptionMode.ManualOnly, "options.mode.manual"),
        new ModeOption(ExceptionMode.Disabled, "options.mode.off"),
    };

    /// <summary>Обновляет подписи всех вариантов списка после смены языка.</summary>
    public static void RefreshTitles(IEnumerable<ChoiceOption> options)
    {
        foreach (ChoiceOption option in options)
        {
            option.RefreshTitle();
        }
    }

    /// <summary>Обновляет подписи всех вариантов списка после смены языка.</summary>
    public static void RefreshTitles(IEnumerable<ModeOption> options)
    {
        foreach (ModeOption option in options)
        {
            option.RefreshTitle();
        }
    }
}
