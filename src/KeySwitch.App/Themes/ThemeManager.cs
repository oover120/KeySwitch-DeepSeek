using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using KeySwitch.App.Services;
using Microsoft.Win32;

namespace KeySwitch.App.Themes;

/// <summary>
/// Подмена словаря цветов и обновление оконных атрибутов Windows 11
/// (тёмная рамка, скруглённые углы). Работает поверх App.Resources:
/// ThemeManager.Apply сам подключает Themes/Colors.*.xaml и Themes/Styles.xaml,
/// поэтому App.xaml может оставаться пустым.
/// </summary>
public static class ThemeManager
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    private static readonly string ThemeAssemblyName =
        typeof(ThemeManager).Assembly.GetName().Name ?? "KeySwitch";

    /// <summary>
    /// Абсолютный pack-URI словаря темы. Относительный «Themes/...» WPF разрешает
    /// от входной сборки приложения; в тестах и харнессах входная сборка чужая,
    /// поэтому адресуем словари явно по имени сборки KeySwitch.
    /// </summary>
    private static Uri ColorsUri(bool dark) => new(
        $"pack://application:,,,/{ThemeAssemblyName};component/Themes/Colors.{(dark ? "Dark" : "Light")}.xaml",
        UriKind.Absolute);

    private static Uri StylesUri => new(
        $"pack://application:,,,/{ThemeAssemblyName};component/Themes/Styles.xaml",
        UriKind.Absolute);

    private static bool _initialized;
    private static string _currentThemeId = "system";
    private static bool _isDark;

    /// <summary>Тема приложения изменилась (системная или выбранная пользователем).</summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>Идентификатор темы: "system" | "light" | "dark".</summary>
    public static string CurrentThemeId => _currentThemeId;

    /// <summary>Активна ли сейчас тёмная палитра.</summary>
    public static bool IsDark => _isDark;

    /// <summary>Применить тему из AppSettings.Theme ("system" | "light" | "dark").</summary>
    public static void Apply(string? themeId)
    {
        string normalized = Normalize(themeId);
        bool dark = normalized switch
        {
            "dark" => true,
            "light" => false,
            _ => IsSystemDark()
        };

        _currentThemeId = normalized;
        _isDark = dark;
        _initialized = true;

        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        ResourceDictionary colors = LoadColors(dark);
        ApplyAccent(colors, dark);
        ReplaceColorsDictionary(app, colors);
        EnsureStyles(app);

        ThemeChanged?.Invoke(null, EventArgs.Empty);
        RefreshOpenWindows();
    }

    /// <summary>Применить тему из enum-значения (объявлен в KeySwitch.App.Services).</summary>
    public static void Apply(AppTheme theme) => Apply(theme switch
    {
        AppTheme.Light => "light",
        AppTheme.Dark => "dark",
        _ => "system"
    });

    /// <summary>Применить тему один раз (например, при старте приложения).</summary>
    public static void EnsureInitialized(string? themeId = null)
    {
        if (_initialized)
        {
            return;
        }

        Apply(themeId);
    }

    /// <summary>Скруглённые углы и тёмная рамка для окна (Win11; на Win10 атрибуты игнорируются).</summary>
    public static void ApplyWindowChrome(Window window)
    {
        if (!window.IsInitialized && !window.IsLoaded)
        {
            window.SourceInitialized += (_, _) => ApplyWindowChromeCore(window);
            return;
        }

        ApplyWindowChromeCore(window);
    }

    /// <summary>Сообщить системе о смене тёмного режима для уже открытых окон.</summary>
    public static void RefreshOpenWindows()
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        foreach (Window window in app.Windows)
        {
            ApplyWindowChromeCore(window);
        }
    }

    /// <summary>Системная тема приложений: true — тёмная.</summary>
    public static bool IsSystemDark()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            object? raw = key?.GetValue("AppsUseLightTheme");
            if (raw is int value)
            {
                return value == 0;
            }
        }
        catch (Exception)
        {
            // реестр недоступен — считаем светлой
        }

        return false;
    }

    /// <summary>Системный акцентный цвет (DWM ColorizationColor, формат 0xAARRGGBB).</summary>
    public static Color GetSystemAccentColor()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("ColorizationColor") is int raw)
            {
                uint argb = unchecked((uint)raw);
                byte r = (byte)((argb >> 16) & 0xFF);
                byte g = (byte)((argb >> 8) & 0xFF);
                byte b = (byte)(argb & 0xFF);
                if (r + g + b > 24)
                {
                    return Color.FromRgb(r, g, b);
                }
            }
        }
        catch (Exception)
        {
            // реестр недоступен
        }

        return Color.FromRgb(0x00, 0x67, 0xC0);
    }

    private static void ApplyWindowChromeCore(Window window)
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            int dark = _isDark ? 1 : 0;
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

            int corner = DwmwcpRound;
            _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
        }
        catch (Exception)
        {
            // dwmapi может отсутствовать (Windows 7/8) — деградируем молча
        }
    }

    private static ResourceDictionary LoadColors(bool dark)
    {
        var dictionary = new ResourceDictionary
        {
            Source = ColorsUri(dark)
        };
        return dictionary;
    }

    private static void ApplyAccent(ResourceDictionary colors, bool dark)
    {
        Color accent = GetSystemAccentColor();
        if (dark && RelativeLuminance(accent) < 0.30)
        {
            // На тёмном фоне тёмный акцент плохо читается — слегка высветляем.
            accent = Blend(accent, Colors.White, 0.22);
        }
        else if (!dark && RelativeLuminance(accent) > 0.75)
        {
            // Светлый акцент (жёлтый, лайм) на белом — притемняем.
            accent = Blend(accent, Colors.Black, 0.20);
        }

        Color hover = Blend(accent, Colors.White, dark ? 0.16 : 0.10);
        Color pressed = Blend(accent, Colors.Black, 0.14);
        Color background = dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3);
        Color subtle = Blend(background, accent, dark ? 0.24 : 0.14);
        Color onAccent = RelativeLuminance(accent) > 0.55 ? Colors.Black : Colors.White;

        colors["AccentBrush"] = Frozen(accent);
        colors["AccentHoverBrush"] = Frozen(hover);
        colors["AccentPressedBrush"] = Frozen(pressed);
        colors["AccentSubtleBrush"] = Frozen(subtle);
        colors["AccentBorderBrush"] = Frozen(accent);
        colors["ControlBorderFocusBrush"] = Frozen(accent);
        colors["TextOnAccentBrush"] = Frozen(onAccent);
    }

    private static void ReplaceColorsDictionary(Application app, ResourceDictionary colors)
    {
        var merged = app.Resources.MergedDictionaries;
        int insertAt = -1;
        for (int i = 0; i < merged.Count; i++)
        {
            ResourceDictionary item = merged[i];
            if (IsColorsSource(item.Source))
            {
                insertAt = i;
                merged.RemoveAt(i);
                break;
            }
        }

        if (insertAt < 0)
        {
            insertAt = 0;
        }

        merged.Insert(Math.Min(insertAt, merged.Count), colors);
    }

    private static void EnsureStyles(Application app)
    {
        foreach (ResourceDictionary item in app.Resources.MergedDictionaries)
        {
            if (item.Source is not null && item.Source.OriginalString.Contains("Styles.xaml", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = StylesUri });
    }

    private static bool IsColorsSource(Uri? source)
    {
        if (source is null)
        {
            return false;
        }

        string text = source.OriginalString;
        return text.Contains("Colors.Light.xaml", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Colors.Dark.xaml", StringComparison.OrdinalIgnoreCase);
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static string Normalize(string? themeId)
    {
        if (string.Equals(themeId, "light", StringComparison.OrdinalIgnoreCase))
        {
            return "light";
        }

        if (string.Equals(themeId, "dark", StringComparison.OrdinalIgnoreCase))
        {
            return "dark";
        }

        return "system";
    }

    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            double c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    private static Color Blend(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
        return Color.FromRgb(Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B));
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
