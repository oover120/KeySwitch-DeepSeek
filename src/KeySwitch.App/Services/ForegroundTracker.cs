using KeySwitch.Data;
using KeySwitch.Hooks;

namespace KeySwitch.App.Services;

/// <summary>
/// Кэш информации об активном окне, живущий ровно столько, сколько фокус стоит в этом окне.
///
/// Почему так: хук обязан отвечать за микросекунды, поэтому он больше не вызывает ни
/// <see cref="WindowInspector"/>, ни UI Automation. Всё это делает рабочий поток, а кэш
/// прогревается заранее — по событию <c>EVENT_SYSTEM_FOREGROUND</c> (см. <see cref="ForegroundWatcher"/>).
/// Первое нажатие после смены фокуса, если прогрев не успел, добирается синхронно — но уже
/// в рабочем потоке, а не в колбэке хука.
/// </summary>
public sealed class ForegroundTracker
{
    private static readonly ForegroundWindowInfo Unknown =
        new(IntPtr.Zero, 0, string.Empty, string.Empty, string.Empty, false, false, false);

    private readonly WindowInspector _inspector;
    private readonly UiAutomationProbe _uia;
    private readonly AppLogger _logger;
    private readonly object _gate = new();

    private IntPtr _handle;
    private ForegroundWindowInfo _info = Unknown;
    private bool _passwordKnown;
    private bool _password;
    private bool _warming;

    public ForegroundTracker(WindowInspector inspector, UiAutomationProbe uia, AppLogger logger)
    {
        _inspector = inspector;
        _uia = uia;
        _logger = logger;
    }

    /// <summary>Сбрасывает кэш (после смены раскладки или настроек).</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _handle = IntPtr.Zero;
            _info = Unknown;
            _passwordKnown = false;
            _password = false;
        }
    }

    /// <summary>
    /// Сменилось активное окно: сбрасываем данные прошлого окна и греем кэш в фоне,
    /// чтобы к первому нажатию всё уже было готово.
    /// </summary>
    public void OnForegroundChanged(IntPtr handle)
    {
        lock (_gate)
        {
            if (_handle == handle)
                return;

            _handle = handle;
            _info = Unknown;
            _passwordKnown = false;
            _password = false;

            if (_warming)
                return; // текущий прогрев сам увидит новое окно и обновит данные

            _warming = true;
        }

        ThreadPool.QueueUserWorkItem(_ => Warm(handle));
    }

    /// <summary>Снимок активного окна. В рабочем потоке стоит микросекунды.</summary>
    public ForegroundWindowInfo Get()
    {
        IntPtr handle = WindowInspector.GetForegroundHandle();

        lock (_gate)
        {
            if (_handle == handle && _info.Handle == handle)
                return _info;
        }

        // Прогрев не успел (или фокус сменился между событием и нажатием) — читаем сейчас.
        return Refresh(handle);
    }

    /// <summary>
    /// Активное поле ввода — пароль? Значение берётся из кэша, прогретого при смене фокуса;
    /// если его ещё нет, проверка выполняется здесь же (это рабочий поток, не хук).
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
            if (_passwordKnown && _handle == handle)
                return _password;
        }

        bool result = ProbePassword(handle);

        lock (_gate)
        {
            if (_handle == handle)
            {
                _password = result;
                _passwordKnown = true;
            }
        }

        return result;
    }

    // ------------------------------------------------------------ внутреннее

    private ForegroundWindowInfo Refresh(IntPtr expected)
    {
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

        if (info.Handle == IntPtr.Zero && expected != IntPtr.Zero)
            info = Unknown with { Handle = expected };

        lock (_gate)
        {
            _handle = expected;
            _info = info;
        }

        return info;
    }

    private void Warm(IntPtr expected)
    {
        try
        {
            var info = _inspector.GetForeground();
            if (info.Handle != expected)
            {
                // Фокус уже ушёл: прогрев устарел, новый запустит OnForegroundChanged.
                return;
            }

            bool password = ProbePassword(info.Handle);

            lock (_gate)
            {
                if (_handle == expected && info.Handle == expected)
                {
                    _info = info;
                    _password = password;
                    _passwordKnown = true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"Прогрев окна: {ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                _warming = false;
            }
        }
    }

    private bool ProbePassword(IntPtr handle)
    {
        try
        {
            return _uia.IsPasswordField(handle);
        }
        catch (Exception ex)
        {
            // Любая ошибка UIA трактуется как «не пароль» — иначе поле пароля
            // навсегда заблокирует обработку в этом окне.
            _logger.Debug($"UIA IsPasswordField: {ex.Message}");
            return false;
        }
    }
}
