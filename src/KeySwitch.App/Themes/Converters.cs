using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Binding = System.Windows.Data.Binding;

namespace KeySwitch.App.Themes;

/// <summary>bool → Visibility (Visible/Collapsed). Параметр "invert" меняет смысл.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        if (IsInverted(parameter))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool visible = value is Visibility v && v == Visibility.Visible;
        return IsInverted(parameter) ? !visible : visible;
    }

    private static bool IsInverted(object? parameter) =>
        parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Обратное логическое значение.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : false;
}

/// <summary>Пустая строка → Collapsed (или Visible при параметре "invert").</summary>
public sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool empty = string.IsNullOrWhiteSpace(value as string);
        bool inverted = parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase);
        return (inverted ? !empty : empty) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>0..1 → «70 %».</summary>
public sealed class PercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double v = value is double d ? d : 0;
        return string.Format(CultureInfo.InvariantCulture, "{0:0} %", Math.Clamp(v, 0, 1) * 100);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Дробное число с заданным количеством знаков: параметр задаёт формат, по умолчанию «0.0».</summary>
public sealed class NumberFormatConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string format = parameter as string ?? "0.0";
        return value switch
        {
            double d => d.ToString(format, CultureInfo.InvariantCulture),
            float f => f.ToString(format, CultureInfo.InvariantCulture),
            _ => value?.ToString() ?? string.Empty
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
