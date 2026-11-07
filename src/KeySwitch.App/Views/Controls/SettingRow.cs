using System.Windows;
using System.Windows.Controls;

namespace KeySwitch.App.Views.Controls;

/// <summary>
/// Строка настройки: заголовок и пояснение слева, элемент управления справа.
/// Разметка задаётся в Themes/Styles.xaml (стиль по типу).
/// </summary>
public class SettingRow : ContentControl
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingRow),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow),
            new PropertyMetadata(string.Empty));

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }
}
