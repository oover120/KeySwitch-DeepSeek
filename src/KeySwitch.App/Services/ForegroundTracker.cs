using KeySwitch.Data;
using KeySwitch.Hooks;

namespace KeySwitch.App.Services;

/// <summary>
/// Кэш информации об активном окне. Каждое нажатие в хуке не должно стоить дорого,
/// поэтому сведения об окне (процесс, раскладка, повышение прав, полный экран) обновляются
/// не чаще, чем раз в 250 мс и при смене HWND. Проверка поля пароля через UI Automation
/// дополнительно кэшируется на 2 секунды — она самая дорогая.
/// </summary>
public sealed class ForegroundTracker
{
    private static readonly ForegroundWindowInfo Unknown =
        new(IntPtr.Zero, 0, string.Empty, string.Empty, string.Empty, false, false, false);

    private readonly WindowInspector _inspector;
    private readonly UiAutomationProbe _uia;
    private readonly AppLogger _logger;
    private readonly object _gate = new();

    private ForegroundWindowInfo? _cached;
    private long _cachedAt;

    private IntPtr _passwordHandle;
    private bool _isPasswordField;
    private long _passwordCheckedAt;

    public ForegroundTracker(WindowInspector inspector, UiAutomationProbe uia, AppLogger logger)
    {
        _inspector = inspector;
        _uia = uia;
        _logger = logger;
    }

    /// <summary>Сбрасывает кэш (после смены раскладки, фокуса или настроек).</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _cached = null;
            _cachedAt = 0;
        }
    }

    public ForegroundWindowInfo Get()
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (_cached is { } cached && now - _cachedAt < 250)
                return cached;
        }

        ForegroundWindowInfo info;
        try
        {
            info = _inspector.GetForeground();
        }
        catch (Exception ex)
        {
            _logger.Debug($"ForegroundTracker: {ex.Message}");
            info = Unknown;
        }

        lock (_gate)
        {
            _cached = info;
            _cachedAt = Environment.TickCount64;
        }

        return info;
    }

    /// <summary>
    /// Активное поле ввода — пароль? Проверка через UIA, не чаще раза в 2 секунды на окно.
    /// </summary>
    public bool IsPasswordField(bool enabled)
    {
        if (!enabled)
            return false;

        var handle = Get().Handle;
        if (handle == IntPtr.Zero)
            return false;

        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (handle == _passwordHandle && now - _passwordCheckedAt < 2000)
                return _isPasswordField;
        }

        var result = false;
        try
        {
            result = _uia.IsPasswordField(handle);
        }
        catch (Exception ex)
        {
            _logger.Debug($"UIA IsPasswordField: {ex.Message}");
        }

        lock (_gate)
        {
            _passwordHandle = handle;
            _isPasswordField = result;
            _passwordCheckedAt = Environment.TickCount64;
        }

        return result;
    }
}
