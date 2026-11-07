using KeySwitch.Data;

namespace KeySwitch.App.ViewModels;

/// <summary>Вариант для ComboBox: идентификатор для настроек + подпись для человека.</summary>
public sealed class ChoiceOption
{
    public ChoiceOption(string value, string title)
    {
        Value = value;
        Title = title;
    }

    public string Value { get; }

    public string Title { get; }

    public override string ToString() => Title;
}

/// <summary>Режим обработки процесса для ComboBox.</summary>
public sealed class ModeOption
{
    public ModeOption(ExceptionMode value, string title)
    {
        Value = value;
        Title = title;
    }

    public ExceptionMode Value { get; }

    public string Title { get; }

    public override string ToString() => Title;
}

/// <summary>Списки вариантов, общие для всех вкладок настроек.</summary>
public static class OptionLists
{
    public static IReadOnlyList<ChoiceOption> Themes { get; } = new[]
    {
        new ChoiceOption("system", "Как в системе"),
        new ChoiceOption("light", "Светлая"),
        new ChoiceOption("dark", "Тёмная"),
    };

    public static IReadOnlyList<ChoiceOption> Languages { get; } = new[]
    {
        new ChoiceOption("ru", "Русский"),
        new ChoiceOption("en", "English"),
        new ChoiceOption("system", "Как в системе"),
    };

    public static IReadOnlyList<ChoiceOption> Directions { get; } = new[]
    {
        new ChoiceOption(ExceptionStore.DirectionBoth, "В обе стороны"),
        new ChoiceOption(ExceptionStore.DirectionRuEn, "RU → EN"),
        new ChoiceOption(ExceptionStore.DirectionEnRu, "EN → RU"),
    };

    public static IReadOnlyList<ChoiceOption> TypoLanguages { get; } = new[]
    {
        new ChoiceOption("ru", "Русский"),
        new ChoiceOption("en", "Английский"),
    };

    public static IReadOnlyList<ModeOption> Modes { get; } = new[]
    {
        new ModeOption(ExceptionMode.Full, "Полная обработка"),
        new ModeOption(ExceptionMode.ManualOnly, "Только вручную"),
        new ModeOption(ExceptionMode.Disabled, "Выключено"),
    };
}
