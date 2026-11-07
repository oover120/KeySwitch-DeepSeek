using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Control = System.Windows.Controls.Control;
using Cursors = System.Windows.Input.Cursors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace KeySwitch.App.Views.Controls;

/// <summary>
/// Поле ввода горячей клавиши: фокусируется мышью/табом, перехватывает нажатие
/// и записывает сочетание в формате <c>Ctrl+Shift+Space</c> (как в AppSettings.ManualConvertHotkey).
/// Backspace/Delete очищают значение.
/// </summary>
public class HotkeyBox : Control
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(string), typeof(HotkeyBox),
            new FrameworkPropertyMetadata(string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    private static readonly DependencyPropertyKey DisplayTextPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(DisplayText), typeof(string), typeof(HotkeyBox),
            new PropertyMetadata("Не задано"));

    public static readonly DependencyProperty DisplayTextProperty = DisplayTextPropertyKey.DependencyProperty;

    private bool _capturing;

    public HotkeyBox()
    {
        Focusable = true;
        IsTabStop = true;
        Cursor = Cursors.IBeam;
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            Focus();
            e.Handled = true;
        };
    }

    /// <summary>Сочетание в формате <c>Ctrl+Shift+Space</c>; пустая строка — не задано.</summary>
    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Текст, который показывает шаблон.</summary>
    public string DisplayText => (string)GetValue(DisplayTextProperty);

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        _capturing = true;
        UpdateDisplayText();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        _capturing = false;
        UpdateDisplayText();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!_capturing)
        {
            return;
        }

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.None)
        {
            UpdateDisplayText();
            e.Handled = true;
            return;
        }

        if (key is Key.Back or Key.Delete)
        {
            SetCurrentValue(ValueProperty, string.Empty);
            UpdateDisplayText();
            e.Handled = true;
            return;
        }

        if (key == Key.Escape)
        {
            _capturing = false;
            Keyboard.ClearFocus();
            UpdateDisplayText();
            e.Handled = true;
            return;
        }

        string combo = BuildCombination(key);
        if (combo.Length > 0)
        {
            SetCurrentValue(ValueProperty, combo);
        }

        UpdateDisplayText();
        e.Handled = true;
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if (_capturing)
        {
            // Не даём клавише «протечь» в окно и сдвинуть фокус.
            e.Handled = true;
        }
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((HotkeyBox)d).UpdateDisplayText();
    }

    private string BuildCombination(Key key)
    {
        string modifiers = ModifiersPrefix();
        string name = KeyName(key);
        if (name.Length == 0)
        {
            return string.Empty;
        }

        return modifiers + name;
    }

    private static string ModifiersPrefix()
    {
        ModifierKeys modifiers = Keyboard.Modifiers;
        var parts = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        return parts.Count == 0 ? string.Empty : string.Join("+", parts) + "+";
    }

    private static string KeyName(Key key)
    {
        if (key is >= Key.D0 and <= Key.D9)
        {
            return ((int)(key - Key.D0)).ToString();
        }

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            return "Num" + (int)(key - Key.NumPad0);
        }

        return key switch
        {
            Key.Space => "Space",
            Key.Oem3 => "OemTilde",
            Key.OemMinus => "OemMinus",
            Key.OemPlus => "OemPlus",
            Key.OemOpenBrackets => "OemOpenBrackets",
            Key.OemCloseBrackets => "OemCloseBrackets",
            Key.OemPipe => "OemPipe",
            Key.OemSemicolon => "OemSemicolon",
            Key.OemQuotes => "OemQuotes",
            Key.OemComma => "OemComma",
            Key.OemPeriod => "OemPeriod",
            Key.OemQuestion => "OemQuestion",
            Key.Back => "Backspace",
            Key.Return => "Enter",
            Key.Escape => "Escape",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Capital => "CapsLock",
            _ => key.ToString()
        };
    }

    private void UpdateDisplayText()
    {
        string text;
        if (_capturing)
        {
            string pending = ModifiersPrefix();
            text = pending.Length == 0 ? "Нажмите сочетание…" : pending + "…";
        }
        else
        {
            text = string.IsNullOrWhiteSpace(Value) ? "Не задано" : Value;
        }

        SetValue(DisplayTextPropertyKey, text);
    }
}
