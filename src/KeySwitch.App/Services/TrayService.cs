using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using KeySwitch.App.Localization;
using KeySwitch.App.Tray;
using KeySwitch.Data;
using KeySwitch.Hooks;

namespace KeySwitch.App.Services;

/// <summary>
/// Значок в трее на чистом Win32: <c>Shell_NotifyIcon</c> плюс скрытое окно-владелец,
/// зарегистрированное напрямую через <c>RegisterClassExW</c>/<c>CreateWindowExW</c>.
///
/// Почему Win32, а не WPF: приложение живёт в трее постоянно, а любой WPF-рендер
/// (HwndSource, RenderTargetBitmap) поднимает milcore → d3d9 → драйвер видеокарты
/// и добавляет к рабочему набору 40–50 МБ. Здесь рисуется только HICON (GDI),
/// меню — нативное, таймер — <c>SetTimer</c>/<c>WM_TIMER</c>, поэтому трей стоит
/// считанные мегабайты.
///
/// Окно-владелец не показывается никогда: без <c>WS_VISIBLE</c> и с
/// <c>WS_EX_TOOLWINDOW</c> его нет ни на панели задач, ни в Alt+Tab. Оно создаётся
/// как top-level (не message-only) сознательно: <c>SetForegroundWindow</c> не может
/// сделать foreground-окном message-only окно, а без foreground контекстное меню
/// не закрывается по клику вне него.
///
/// Иконка обновляется при смене раскладки, включении/выключении автопереключения
/// и переходе в ручной режим: опрос раз в 500 мс плюс немедленное обновление по событиям.
/// </summary>
public sealed class TrayService : IDisposable
{
    /// <summary>Идентификатор значка внутри окна (uID); одно окно — один значок.</summary>
    private const uint TrayIconId = 1;

    /// <summary>Идентификатор таймера обновления статуса.</summary>
    private static readonly IntPtr StatusTimerId = 1;

    /// <summary>Период опроса состояния, мс.</summary>
    private const uint StatusTimerIntervalMs = 500;

    /// <summary>Имя зарегистрированного класса окна трея.</summary>
    private const string WindowClassName = "KeySwitch.TrayMessageWindow";

    /// <summary>Резервный номер сообщения, если RegisterWindowMessage почему-то не сработал.</summary>
    private const int WM_APP = 0x8000;
    private const int WM_TRAYICON_FALLBACK = WM_APP + 1;

    private const int WM_NULL = 0x0000;
    private const int WM_TIMER = 0x0113;
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_DPICHANGED = 0x02E0;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int NIN_SELECT = 0x0400;          // WM_USER + 0
    private const int NIN_KEYSELECT = 0x0401;       // WM_USER + 1

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIM_SETVERSION = 0x00000004;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;
    private const uint NIF_SHOWTIP = 0x00000080;

    private const uint NIIF_INFO = 0x00000001;
    private const uint NIIF_WARNING = 0x00000002;
    private const uint NIIF_ERROR = 0x00000003;

    private const uint NOTIFYICON_VERSION_4 = 4;

    private const int WS_EX_TOOLWINDOW = 0x00000080;

    // Пункты контекстного меню.
    private const uint CommandToggleAuto = 1;
    private const uint CommandExceptions = 2;
    private const uint CommandSettings = 3;
    private const uint CommandAutostart = 4;
    private const uint CommandAdmin = 5;
    private const uint CommandExit = 6;
    private const uint CommandUpdate = 7;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint MF_CHECKED = 0x00000008;
    private const uint MF_GRAYED = 0x00000001;

    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_NONOTIFY = 0x0080;
    private const uint TPM_RETURNCMD = 0x0100;

    /// <summary>Штатный таймаут баллуна, мс.</summary>
    private const uint BalloonTimeoutMs = 4000;

    /// <summary>szTip в NOTIFYICONDATAW v4 — 128 символов вместе с завершающим нулём.</summary>
    private const int MaxTipLength = 127;

    /// <summary>szInfoTitle — 64 символа с нулём, szInfo — 256.</summary>
    private const int MaxInfoTitleLength = 63;
    private const int MaxInfoLength = 255;

    /// <summary>Больше стольких HICON в кеше не держим (страховка от «дырявых» layoutId).</summary>
    private const int MaxCachedIcons = 24;

    private readonly AppSettings _settings;
    private readonly AutoSwitchService _auto;
    private readonly ForegroundTracker _tracker;
    private readonly ExceptionStore _exceptions;
    private readonly AutostartService _autostart;
    private readonly AppLogger _logger;
    private readonly Dispatcher _dispatcher;

    // Окно-владелец. Делегат WndProc держим в поле: если его соберёт GC, процесс упадёт
    // на первом же сообщении оболочки.
    private readonly WindowProcDelegate _wndProc;
    private readonly IntPtr _wndProcPtr;
    private readonly IntPtr _moduleHandle;
    private IntPtr _hwnd;
    private bool _classRegistered;
    private bool _windowDestroyed;

    private readonly uint _wmTrayIcon;
    private readonly uint _wmTaskbarCreated;

    // Кеш нарисованных HICON: состояние -> иконка. Все иконки живут до Dispose —
    // оболочка может ещё держать ссылку на предыдущую, поэтому сразу их не удаляем.
    private readonly Dictionary<string, IntPtr> _iconCache = new(StringComparer.Ordinal);
    private int _iconCachePixelSize;

    // Окна с повышенными правами: про UIPI предупреждаем один раз на окно.
    private readonly HashSet<IntPtr> _elevatedWarned = new();

    private IntPtr _currentHIcon;
    private bool _iconAdded;
    private bool _forceIconRefresh;
    private bool _menuOpen;
    private bool _timerCreated;
    private DateTime _nextTrayAddAttemptUtc = DateTime.MinValue;

    private string _lastLayout = "\0";
    private bool _lastEnabled;
    private bool _lastManualOnly;
    private bool _lastStarted = true;
    private string _lastTooltip = string.Empty;
    private bool _disposed;

    public TrayService(
        AppSettings settings,
        AutoSwitchService auto,
        ForegroundTracker tracker,
        ExceptionStore exceptions,
        AutostartService autostart,
        AppLogger logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _auto = auto ?? throw new ArgumentNullException(nameof(auto));
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _exceptions = exceptions ?? throw new ArgumentNullException(nameof(exceptions));
        _autostart = autostart ?? throw new ArgumentNullException(nameof(autostart));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (!_dispatcher.CheckAccess())
        {
            throw new InvalidOperationException("TrayService создаётся только в UI-потоке: значок трея живёт в его message loop.");
        }

        // Собственное сообщение оболочки регистрируем через RegisterWindowMessage: так оно
        // гарантированно не пересечётся с WM_APP-номерами других окон в этом процессе.
        // Fallback — WM_APP+1 на случай, если регистрация не удалась.
        _wmTrayIcon = RegisterWindowMessageW("KeySwitch.TrayIcon");
        if (_wmTrayIcon == 0)
        {
            _wmTrayIcon = (uint)WM_TRAYICON_FALLBACK;
            _logger.Warn("RegisterWindowMessage(\"KeySwitch.TrayIcon\") не удался — использую WM_APP+1.");
        }

        _wmTaskbarCreated = RegisterWindowMessageW("TaskbarCreated");

        _wndProc = WindowProc;
        _wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProc);
        _moduleHandle = GetModuleHandleW(null);

        CreateMessageWindow();

        _auto.StateChanged += OnAutoStateChanged;

        // Подсказка значка хранится в поле готовой строкой, поэтому при смене языка
        // её нужно пересчитать сразу, а не ждать следующего тика таймера.
        Loc.Instance.PropertyChanged += OnLanguageChanged;

        CreateInitialIcon();
        AddIcon();
        RefreshCore();

        StartStatusTimer();
    }

    /// <summary>Пользователь выбрал «Настройки…» (или щёлкнул по значку).</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Пользователь выбрал «Исключения…».</summary>
    public event EventHandler? ExceptionsRequested;

    /// <summary>Пользователь выбрал «Выход».</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Пользователь выбрал «Проверить обновления».</summary>
    public event EventHandler? UpdateRequested;

    /// <summary>Показывает уведомление из трея (например, о неудачном изменении автозапуска).</summary>
    public void ShowBalloon(string title, string text, TrayBalloonIcon icon = TrayBalloonIcon.Info)
    {
        if (_disposed)
        {
            return;
        }

        Post(() => ShowBalloonCore(title, text, icon));
    }

    /// <summary>Немедленно обновить иконку и подсказку (после смены настроек).</summary>
    public void RefreshNow()
    {
        Post(RefreshCore);
    }

    /// <summary>
    /// Регистрирует класс окна и создаёт скрытое окно-владелец значка.
    /// Окно top-level: message-only окно нельзя сделать foreground-окном, а это нужно
    /// для корректного закрытия контекстного меню по клику вне его.
    /// </summary>
    private void CreateMessageWindow()
    {
        var windowClass = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = 0,
            lpfnWndProc = _wndProcPtr,
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = _moduleHandle,
            hIcon = IntPtr.Zero,
            hCursor = IntPtr.Zero,
            hbrBackground = IntPtr.Zero,
            lpszMenuName = null,
            lpszClassName = WindowClassName,
            hIconSm = IntPtr.Zero,
        };

        ushort atom = RegisterClassExW(ref windowClass);
        if (atom == 0)
        {
            int error = Marshal.GetLastWin32Error();
            const int ERROR_CLASS_ALREADY_EXISTS = 1410;

            if (error != ERROR_CLASS_ALREADY_EXISTS)
            {
                _logger.Warn($"Не удалось зарегистрировать класс окна трея, код Win32: {error}.");
            }
        }
        else
        {
            _classRegistered = true;
        }

        // WS_EX_TOOLWINDOW убирает окно из Alt+Tab; WS_VISIBLE не задаём — окно скрыто.
        // Стиль 0 — обычное top-level окно без рамки.
        _hwnd = CreateWindowExW(
            WS_EX_TOOLWINDOW,
            WindowClassName,
            "KeySwitch DeepSeek",
            0,
            -32000,
            -32000,
            1,
            1,
            IntPtr.Zero,
            IntPtr.Zero,
            _moduleHandle,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            _logger.Error($"Не удалось создать скрытое окно трея (CreateWindowExW), код Win32: {error}.");
            throw new InvalidOperationException($"CreateWindowExW для окна трея вернул NULL, код Win32: {error}.");
        }
    }

    private void StartStatusTimer()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        // SetTimer шлёт WM_TIMER в это же окно: никакого WPF-таймера и лишних объектов.
        if (SetTimer(_hwnd, StatusTimerId, StatusTimerIntervalMs, IntPtr.Zero) == IntPtr.Zero)
        {
            _logger.Warn($"Не удалось запустить таймер трея (SetTimer), код Win32: {Marshal.GetLastWin32Error()}.");
            return;
        }

        _timerCreated = true;
    }

    private void OnAutoStateChanged(object? sender, EventArgs e)
    {
        // Событие может прийти из потока хука (Ctrl+Alt+P), поэтому всегда через Dispatcher.
        Post(RefreshCore);
    }

    /// <summary>
    /// Сменился язык интерфейса: меню собирается заново при каждом открытии и язык берёт
    /// из <see cref="Loc"/> само, а подсказка лежит готовой строкой — сбрасываем её,
    /// чтобы следующий <see cref="RefreshCore"/> пересобрал текст.
    /// </summary>
    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        _lastTooltip = string.Empty;
        Post(RefreshCore);
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (_disposed)
        {
            return DefWindowProcW(hwnd, message, wParam, lParam);
        }

        if (message == _wmTrayIcon)
        {
            // NOTIFYICON_VERSION_4: lParam — LOWORD уведомление, HIWORD идентификатор значка
            // (у нас он всегда TrayIconId), wParam — экранные координаты курсора.
            int notification = (int)(lParam.ToInt64() & 0xFFFF);
            switch (notification)
            {
                case NIN_SELECT:
                case NIN_KEYSELECT:
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                    RequestSettings();
                    break;

                case WM_CONTEXTMENU:
                case WM_RBUTTONUP:
                    RequestContextMenu();
                    break;
            }

            return IntPtr.Zero;
        }

        if (_wmTaskbarCreated != 0 && message == _wmTaskbarCreated)
        {
            // Проводник (и панель задач) перезапустился — значок нужно добавить заново.
            _iconAdded = false;
            _nextTrayAddAttemptUtc = DateTime.MinValue;
            Post(RefreshCore);
            return IntPtr.Zero;
        }

        switch (message)
        {
            case WM_TIMER:
                if (wParam == StatusTimerId)
                {
                    RefreshCore();
                }

                return IntPtr.Zero;

            case WM_DPICHANGED:
            case WM_SETTINGCHANGE:
                // DPI/метрики изменились — HICON нужно нарисовать заново под новый размер.
                _forceIconRefresh = true;
                break;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void RequestSettings()
    {
        if (_disposed)
        {
            return;
        }

        // Открываем настройки после возврата из обработчика сообщения: ShowDialog запускает
        // собственный цикл, и делать это прямо внутри WndProc не стоит.
        PostDeferred(() =>
        {
            if (!_disposed)
            {
                SettingsRequested?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    private void RequestContextMenu()
    {
        if (_disposed)
        {
            return;
        }

        PostDeferred(ShowContextMenu);
    }

    /// <summary>
    /// Собирает нативное popup-меню, показывает его и разбирает выбранный пункт.
    /// Меню создаётся заново на каждый показ: состояние галочек берётся из настроек
    /// в момент открытия, поэтому рассинхронизироваться с ними не может.
    /// </summary>
    private void ShowContextMenu()
    {
        if (_disposed || _menuOpen || _hwnd == IntPtr.Zero)
        {
            return;
        }

        _menuOpen = true;
        IntPtr menu = IntPtr.Zero;

        try
        {
            menu = CreatePopupMenu();
            if (menu == IntPtr.Zero)
            {
                _logger.Warn($"Не удалось создать меню трея (CreatePopupMenu), код Win32: {Marshal.GetLastWin32Error()}.");
                return;
            }

            bool autoEnabled = _settings.Enabled;
            AppendMenuW(
                menu,
                MF_STRING | (autoEnabled ? MF_CHECKED : 0),
                (UIntPtr)CommandToggleAuto,
                Loc.T(autoEnabled ? "tray.autoswitch.on" : "tray.autoswitch.off"));

            AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenuW(menu, MF_STRING, (UIntPtr)CommandExceptions, Loc.T("tray.exceptions"));
            AppendMenuW(menu, MF_STRING, (UIntPtr)CommandSettings, Loc.T("tray.settings"));
            AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);

            AppendMenuW(
                menu,
                MF_STRING | (_settings.StartWithWindows ? MF_CHECKED : 0),
                (UIntPtr)CommandAutostart,
                Loc.T("tray.autostart"));

            // Запуск от администратора бессмыслен без автозапуска — приглушаем пункт.
            AppendMenuW(
                menu,
                MF_STRING | (_settings.RunAsAdmin ? MF_CHECKED : 0) | (_settings.StartWithWindows ? 0 : MF_GRAYED),
                (UIntPtr)CommandAdmin,
                Loc.T("tray.runasadmin"));

            AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenuW(menu, MF_STRING, (UIntPtr)CommandUpdate, Loc.T("tray.update"));
            AppendMenuW(menu, MF_STRING, (UIntPtr)CommandExit, Loc.T("tray.exit"));

            // Окно трея скрыто и неактивно. Без SetForegroundWindow меню не закроется
            // по клику вне его: первый клик достанется другому окну.
            SetForegroundWindow(_hwnd);

            GetCursorPos(out POINT cursor);
            uint command = (uint)TrackPopupMenuEx(
                menu,
                TPM_LEFTALIGN | TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY,
                cursor.X,
                cursor.Y,
                _hwnd,
                IntPtr.Zero);

            // Возвращаем фокус системе: иначе после закрытия меню остаётся «залипшее» состояние.
            PostMessageW(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (command != 0)
            {
                DispatchCommand(command);
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"TrayService: не удалось показать меню трея: {ex.Message}");
        }
        finally
        {
            if (menu != IntPtr.Zero)
            {
                DestroyMenu(menu);
            }

            _menuOpen = false;
        }
    }

    private void DispatchCommand(uint command)
    {
        Action? action = command switch
        {
            CommandToggleAuto => () =>
            {
                _auto.ToggleEnabled();
                RefreshNow();
            },
            CommandExceptions => () => ExceptionsRequested?.Invoke(this, EventArgs.Empty),
            CommandSettings => () => SettingsRequested?.Invoke(this, EventArgs.Empty),
            CommandAutostart => ToggleAutostart,
            CommandAdmin => ToggleRunAsAdmin,
            CommandUpdate => () => UpdateRequested?.Invoke(this, EventArgs.Empty),
            CommandExit => () => ExitRequested?.Invoke(this, EventArgs.Empty),
            _ => null,
        };

        if (action is null)
        {
            return;
        }

        // Команда может открыть модальное окно — выполняем её после того, как меню
        // полностью закрыто и уничтожено.
        PostDeferred(action);
    }

    private void ToggleAutostart()
    {
        if (_disposed)
        {
            return;
        }

        bool target = !_settings.StartWithWindows;
        bool ok = _autostart.SetStartWithWindows(target);

        if (!ok && target)
        {
            ShowBalloon(
                Loc.T("balloon.autostart.title"),
                Loc.T("balloon.autostart.failed"),
                TrayBalloonIcon.Warning);
        }

        RefreshNow();
    }

    private void ToggleRunAsAdmin()
    {
        if (_disposed)
        {
            return;
        }

        bool target = !_settings.RunAsAdmin;
        bool ok = _autostart.SetRunAsAdmin(target);

        if (!ok && target)
        {
            // Задачу с RL HIGHEST из обычного процесса не создать. Перезапускаемся
            // с запросом UAC: повышенный экземпляр заберёт мьютекс по --takeover
            // и создаст задачу сам (AutostartService.Synchronize).
            if (ElevationHelper.RestartElevated("--takeover"))
            {
                ExitRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            // Пользователь отказался от UAC — не оставляем галочку, которая врёт.
            _autostart.SetRunAsAdmin(false);
            ShowBalloon(
                Loc.T("balloon.admin.title"),
                Loc.T("balloon.admin.elevation_required"),
                TrayBalloonIcon.Warning);
        }

        RefreshNow();
    }

    private void RefreshCore()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (!_iconAdded && DateTime.UtcNow >= _nextTrayAddAttemptUtc)
            {
                AddIcon();
            }

            ForegroundWindowInfo info;
            try
            {
                info = _tracker.Get();
            }
            catch (Exception)
            {
                info = new ForegroundWindowInfo(IntPtr.Zero, 0, string.Empty, string.Empty, string.Empty, false, false, false);
            }

            bool enabled = _settings.Enabled;
            bool started = _auto.IsStarted;
            bool manualOnly = info.ProcessId != 0 &&
                              _exceptions.GetMode(info.ProcessName, info.ProcessPath) == ExceptionMode.ManualOnly;

            bool layoutChanged = !string.Equals(info.LayoutId, _lastLayout, StringComparison.OrdinalIgnoreCase);
            bool iconNeedsUpdate = layoutChanged ||
                                   enabled != _lastEnabled ||
                                   manualOnly != _lastManualOnly ||
                                   started != _lastStarted ||
                                   _forceIconRefresh;

            if (iconNeedsUpdate && UpdateIcon(info.LayoutId, enabled, manualOnly))
            {
                _lastLayout = info.LayoutId;
                _lastEnabled = enabled;
                _lastManualOnly = manualOnly;
                _lastStarted = started;
                _forceIconRefresh = false;
            }

            UpdateTooltip(BuildTooltip(info.LayoutId, enabled, started, manualOnly));
            WarnAboutElevatedWindow(info);
        }
        catch (Exception ex)
        {
            _logger.Debug($"TrayService.Refresh: {ex.Message}");
        }
    }

    private void CreateInitialIcon()
    {
        try
        {
            _currentHIcon = GetOrCreateIcon("en", enabled: true, manualOnly: false);
        }
        catch (Exception ex)
        {
            _logger.Error("Не удалось нарисовать начальную иконку трея", ex);
            _currentHIcon = IntPtr.Zero;
        }
    }

    /// <summary>Берёт HICON нужного состояния из кеша, рисуя его при первом обращении.</summary>
    private IntPtr GetOrCreateIcon(string layoutId, bool enabled, bool manualOnly)
    {
        int pixelSize = TrayIconFactory.GetIconSize();
        if (pixelSize != _iconCachePixelSize)
        {
            // DPI/метрики поменялись — старые иконки нарисованы под другой размер.
            ClearIconCache();
            _iconCachePixelSize = pixelSize;
        }

        string key = $"{layoutId}|{(enabled ? 1 : 0)}|{(manualOnly ? 1 : 0)}";
        if (_iconCache.TryGetValue(key, out IntPtr cached))
        {
            return cached;
        }

        if (_iconCache.Count >= MaxCachedIcons)
        {
            ClearIconCache();
        }

        IntPtr icon = TrayIconFactory.Create(layoutId, enabled, manualOnly);
        _iconCache[key] = icon;
        return icon;
    }

    private void ClearIconCache()
    {
        foreach (IntPtr icon in _iconCache.Values)
        {
            if (icon != IntPtr.Zero)
            {
                DestroyIcon(icon);
            }
        }

        _iconCache.Clear();
    }

    /// <summary>Создаёт HICON под текущее состояние и отдаёт оболочке. false — попробуем на следующем тике.</summary>
    private bool UpdateIcon(string layoutId, bool enabled, bool manualOnly)
    {
        IntPtr icon;
        try
        {
            icon = GetOrCreateIcon(layoutId, enabled, manualOnly);
        }
        catch (Exception ex)
        {
            _logger.Error("Не удалось нарисовать иконку трея", ex);
            return false;
        }

        if (!_iconAdded)
        {
            // Значок ещё не добавлен (например, оболочка была недоступна) — AddIcon возьмёт
            // новый HICON из _currentHIcon на следующем тике.
            _currentHIcon = icon;
            return true;
        }

        var data = CreateData();
        data.uFlags = NIF_ICON;
        data.hIcon = icon;

        if (!Shell_NotifyIconW(NIM_MODIFY, ref data))
        {
            _logger.Warn($"Не удалось обновить иконку трея (NIM_MODIFY), код Win32: {Marshal.GetLastWin32Error()}.");
            return false;
        }

        // Старую иконку не DestroyIcon'им: она осталась в кеше, а оболочка могла ещё
        // держать на неё ссылку. Все HICON освобождаются разом в Dispose.
        _currentHIcon = icon;
        return true;
    }

    private void UpdateTooltip(string tooltip)
    {
        if (string.Equals(tooltip, _lastTooltip, StringComparison.Ordinal))
        {
            return;
        }

        _lastTooltip = tooltip;

        if (!_iconAdded)
        {
            return;
        }

        var data = CreateData();
        data.uFlags = NIF_TIP | NIF_SHOWTIP;
        data.szTip = Truncate(tooltip, MaxTipLength);

        if (!Shell_NotifyIconW(NIM_MODIFY, ref data))
        {
            _logger.Debug($"Не удалось обновить подсказку трея (NIM_MODIFY), код Win32: {Marshal.GetLastWin32Error()}.");
        }
    }

    private bool AddIcon()
    {
        if (_iconAdded)
        {
            return true;
        }

        if (_hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (_currentHIcon == IntPtr.Zero)
        {
            CreateInitialIcon();
            if (_currentHIcon == IntPtr.Zero)
            {
                _nextTrayAddAttemptUtc = DateTime.UtcNow.AddSeconds(5);
                return false;
            }
        }

        var data = CreateData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        data.uCallbackMessage = _wmTrayIcon;
        data.hIcon = _currentHIcon;
        data.szTip = Truncate(_lastTooltip, MaxTipLength);

        if (!Shell_NotifyIconW(NIM_ADD, ref data))
        {
            int error = Marshal.GetLastWin32Error();
            _logger.Warn($"Не удалось добавить значок в трей (Shell_NotifyIcon NIM_ADD), код Win32: {error}.");
            // Не спамим журнал: следующая попытка не раньше чем через 5 секунд.
            _nextTrayAddAttemptUtc = DateTime.UtcNow.AddSeconds(5);
            return false;
        }

        // NOTIFYICON_VERSION_4: клики приходят как NIN_SELECT/WM_CONTEXTMENU, а тултип
        // работает через NIF_SHOWTIP (стандартная подсказка вместо баллуна).
        var versionData = CreateData();
        versionData.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
        if (!Shell_NotifyIconW(NIM_SETVERSION, ref versionData))
        {
            _logger.Warn(
                $"Не удалось включить NOTIFYICON_VERSION_4 (NIM_SETVERSION), код Win32: {Marshal.GetLastWin32Error()}.");
        }

        _iconAdded = true;
        _logger.Info("Трей создан (Shell_NotifyIcon).");
        return true;
    }

    private void ShowBalloonCore(string title, string text, TrayBalloonIcon icon)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (!_iconAdded)
            {
                _logger.Debug($"Уведомление «{title}» не показано: значок трея ещё не добавлен.");
                return;
            }

            var data = CreateData();
            data.uFlags = NIF_INFO;
            data.szInfoTitle = Truncate(title, MaxInfoTitleLength);
            data.szInfo = Truncate(text, MaxInfoLength);
            data.uTimeoutOrVersion = BalloonTimeoutMs;
            data.dwInfoFlags = icon switch
            {
                TrayBalloonIcon.Warning => NIIF_WARNING,
                TrayBalloonIcon.Error => NIIF_ERROR,
                _ => NIIF_INFO,
            };

            if (!Shell_NotifyIconW(NIM_MODIFY, ref data))
            {
                _logger.Warn(
                    $"Не удалось показать уведомление (Shell_NotifyIcon NIF_INFO), код Win32: {Marshal.GetLastWin32Error()}.");
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"Не удалось показать уведомление: {ex.Message}");
        }
    }

    private string BuildTooltip(string layoutId, bool enabled, bool started, bool manualOnly)
    {
        string language = DescribeLanguage(layoutId);
        string status = !started
            ? Loc.T("tray.status.safe")
            : !enabled
                ? Loc.T("tray.status.disabled")
                : manualOnly
                    ? Loc.T("tray.status.manual")
                    : Loc.T("tray.status.enabled");

        return Truncate(Loc.Format("tray.tooltip", language, status), MaxTipLength);
    }

    private static string DescribeLanguage(string layoutId)
    {
        if (string.IsNullOrEmpty(layoutId))
        {
            return Loc.T("tray.layout.none");
        }

        if (layoutId.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
        {
            return Loc.T("tray.layout.ru");
        }

        if (layoutId.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return Loc.T("tray.layout.en");
        }

        return Loc.T("tray.layout.none");
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Length <= maxLength ? text : text[..maxLength];
    }

    /// <summary>
    /// Подсказка при старте: настройка «Запускать от администратора» включена, но процесс
    /// запущен без прав — значит задача планировщика не создана и её не создать отсюда.
    /// </summary>
    public void ShowAdminRestartHint()
    {
        if (_disposed)
        {
            return;
        }

        ShowBalloon(
            Loc.T("balloon.admin.title"),
            Loc.T("balloon.admin.not_elevated"),
            TrayBalloonIcon.Info);
    }

    private void WarnAboutElevatedWindow(ForegroundWindowInfo info)    {
        if (info.Handle == IntPtr.Zero || !info.IsElevated || ElevationHelper.IsProcessElevated)
        {
            return;
        }

        if (!_elevatedWarned.Add(info.Handle))
        {
            return;
        }

        ShowBalloon(
            Loc.T("app.name"),
            Loc.T("balloon.uipi.text"),
            TrayBalloonIcon.Warning);
    }

    private NOTIFYICONDATAW CreateData() => new()
    {
        cbSize = NotifyIconDataSize,
        hWnd = _hwnd,
        uID = TrayIconId,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private void Post(Action action)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (_dispatcher.CheckAccess())
            {
                action();
                return;
            }

            _dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
        }
        catch (Exception)
        {
            // Диспетчер уже остановлен (выход из приложения) — делать нечего.
        }
    }

    private void PostDeferred(Action action)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
        }
        catch (Exception)
        {
            // Диспетчер уже остановлен (выход из приложения) — делать нечего.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _auto.StateChanged -= OnAutoStateChanged;
        }
        catch (Exception)
        {
            // ignore
        }

        try
        {
            Loc.Instance.PropertyChanged -= OnLanguageChanged;
        }
        catch (Exception)
        {
            // ignore
        }

        try
        {
            if (_timerCreated && _hwnd != IntPtr.Zero)
            {
                KillTimer(_hwnd, StatusTimerId);
                _timerCreated = false;
            }
        }
        catch (Exception)
        {
            // ignore
        }

        try
        {
            if (_iconAdded)
            {
                var data = CreateData();
                if (!Shell_NotifyIconW(NIM_DELETE, ref data))
                {
                    _logger.Debug($"Не удалось удалить значок из трея (NIM_DELETE), код Win32: {Marshal.GetLastWin32Error()}.");
                }

                _iconAdded = false;
            }
        }
        catch (Exception)
        {
            // ignore
        }

        ClearIconCache();
        _currentHIcon = IntPtr.Zero;

        try
        {
            if (_hwnd != IntPtr.Zero && !_windowDestroyed)
            {
                DestroyWindow(_hwnd);
                _windowDestroyed = true;
            }

            _hwnd = IntPtr.Zero;
        }
        catch (Exception)
        {
            // ignore
        }

        try
        {
            if (_classRegistered)
            {
                UnregisterClassW(WindowClassName, _moduleHandle);
                _classRegistered = false;
            }
        }
        catch (Exception)
        {
            // ignore
        }
    }

    private static readonly int NotifyIconDataSize = Marshal.SizeOf<NOTIFYICONDATAW>();

    private delegate IntPtr WindowProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "SetTimer", SetLastError = true)]
    private static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll", EntryPoint = "KillTimer", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int TrackPopupMenuEx(
        IntPtr hMenu,
        uint uFlags,
        int x,
        int y,
        IntPtr hWnd,
        IntPtr lptpm);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        /// <summary>Объединение uTimeout/uVersion: таймаут баллуна либо версия протокола.</summary>
        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }
}
