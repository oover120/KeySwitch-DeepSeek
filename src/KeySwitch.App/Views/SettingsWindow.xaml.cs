using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using KeySwitch.App.Themes;
using KeySwitch.App.ViewModels;

namespace KeySwitch.App.Views;

/// <summary>
/// Окно настроек KeySwitch: кастомный заголовок (WindowChrome), пять вкладок,
/// нижняя панель с сохранением. Живёт на данных ViewModel — сама ничего не пишет.
/// </summary>
public partial class SettingsWindow : Window
{
    private SettingsViewModel? _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        // Тема нужна до разбора XAML: на неё ссылаются стили и кисти.
        ThemeManager.Apply(viewModel.ThemeValue);

        InitializeComponent();

        DataContext = viewModel;
        _viewModel = viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ThemeManager.ThemeChanged += OnThemeChanged;

        Loaded += OnLoaded;
        Closed += OnClosed;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Только для конструктора XAML — без данных.</summary>
    private SettingsWindow()
    {
        ThemeManager.EnsureInitialized();
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThemeManager.ApplyWindowChrome(this);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        ThemeManager.ApplyWindowChrome(this);
    }

    /// <summary>Смена темы в ComboBox применяется сразу — предпросмотр без сохранения.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.ThemeValue) && _viewModel is not null)
        {
            ThemeManager.Apply(_viewModel.ThemeValue);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Кнопка мыши уже отпущена — перетаскивание не начинаем.
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel)
        {
            return;
        }

        if (viewModel.Apply())
        {
            Close();
        }
    }
}
