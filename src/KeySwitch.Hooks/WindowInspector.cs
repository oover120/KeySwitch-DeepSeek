using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace KeySwitch.Hooks;

/// <summary>
/// Снимок активного окна: всё, что нужно конвейеру исправления, одним объектом.
/// <see cref="Handle"/> = <see cref="IntPtr.Zero"/>, если активного окна нет.
/// </summary>
public sealed record ForegroundWindowInfo(
    IntPtr Handle,
    int ProcessId,
    string ProcessName,
    string ProcessPath,
    string LayoutId,
    bool IsElevated,
    bool IsFullscreen,
    bool IsConsole,
    string ClassName = "");

/// <summary>
/// Инспекция окна переднего плана. Результат кэшируется на <see cref="CacheTtlMs"/>:
/// хук вызывает это на каждое нажатие, а разбор процесса и токена — дорогие операции.
/// </summary>
public sealed class WindowInspector
{
    private const int CacheTtlMs = 200;
    private const int FullscreenTolerancePx = 2;

    /// <summary>
    /// Сведения о процессе (путь, имя, повышение прав) живут дольше окна: они не меняются,
    /// пока процесс жив. Кэш по pid экономит OpenProcess на каждом нажатии.
    /// </summary>
    private const int ProcessCacheTtlMs = 30_000;
    private const int MaxProcessEntries = 64;

    private static readonly string[] ConsoleClasses =
    [
        "ConsoleWindowClass",
        "CASCADIA_HOSTING_WINDOW_CLASS"
    ];

    private readonly object _gate = new();
    private readonly LayoutSwitcher _layouts = new();

    private ForegroundWindowInfo? _cached;
    private long _cachedAt;
    private readonly Dictionary<int, ProcessEntry> _processes = new();

    /// <summary>Снимок окна переднего плана; если окна нет — объект с нулевым Handle.</summary>
    public ForegroundWindowInfo GetForeground() =>
        TryGetForeground(out var info) ? info : Empty;

    /// <summary>
    /// HWND активного окна без разбора процесса. Вызов стоит доли микросекунды —
    /// им пользуется кэш, чтобы понять, что фокус всё ещё в том же окне.
    /// </summary>
    public static IntPtr GetForegroundHandle() => NativeMethods.GetForegroundWindow();

    /// <summary>Пытается получить снимок окна переднего плана.</summary>
    public bool TryGetForeground(out ForegroundWindowInfo info)
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            info = Empty;
            return false;
        }

        lock (_gate)
        {
            if (_cached is { } cached &&
                cached.Handle == foreground &&
                Environment.TickCount64 - _cachedAt < CacheTtlMs)
            {
                info = cached;
                return true;
            }
        }

        ForegroundWindowInfo fresh = Inspect(foreground);

        lock (_gate)
        {
            _cached = fresh;
            _cachedAt = Environment.TickCount64;
        }

        info = fresh;
        return true;
    }

    private static ForegroundWindowInfo Empty { get; } =
        new(IntPtr.Zero, 0, string.Empty, string.Empty, string.Empty, false, false, false);

    private ForegroundWindowInfo Inspect(IntPtr window)
    {
        uint threadId = NativeMethods.GetWindowThreadProcessId(window, out uint processId);
        if (threadId == 0)
            threadId = NativeMethods.GetCurrentThreadId();

        int pid = unchecked((int)processId);
        var (processName, processPath, elevated) = GetProcessInfo(pid);

        string layoutId = LayoutIds.FromHkl(NativeMethods.GetKeyboardLayout(threadId));
        bool fullscreen = IsFullscreenWindow(window);
        bool console = IsConsoleWindow(window);

        return new ForegroundWindowInfo(
            window, pid, processName, processPath, layoutId, elevated, fullscreen, console,
            NativeMethods.GetWindowClass(window));
    }

    /// <summary>
    /// Имя, путь и признак повышения прав процесса — из Win32, с кэшем по pid.
    /// </summary>
    /// <remarks>
    /// Здесь сознательно НЕ используется <see cref="Process.GetProcessById"/> с
    /// <c>Process.MainModule</c>: на больших процессах (Chrome, Electron) этот вызов
    /// перечисляет все модули и занимает сотни миллисекунд — замер на Chrome дал 777 мс.
    /// Хук вызывается синхронно из низкоуровневого колбэка, и такая задержка означает
    /// пропуск нажатий и снятие хука системой. <c>QueryFullProcessImageNameW</c> на том же
    /// процессе отвечает за 37 мс, а с кэшем — за единицы микросекунд.
    /// </remarks>
    private (string Name, string Path, bool Elevated) GetProcessInfo(int pid)
    {
        if (pid <= 0)
            return (string.Empty, string.Empty, false);

        lock (_gate)
        {
            if (_processes.TryGetValue(pid, out ProcessEntry entry) &&
                Environment.TickCount64 - entry.Stamp < ProcessCacheTtlMs)
            {
                return (entry.Name, entry.Path, entry.Elevated);
            }
        }

        string path = QueryProcessPath(pid);
        string name = string.Empty;
        try
        {
            name = System.IO.Path.GetFileNameWithoutExtension(path);
        }
        catch (ArgumentException)
        {
            // Недопустимые символы в пути — оставляем имя пустым.
        }

        bool elevated = ElevationHelper.IsElevatedProcess(pid);

        lock (_gate)
        {
            if (_processes.Count >= MaxProcessEntries)
                TrimProcesses();

            _processes[pid] = new ProcessEntry(name, path, elevated, Environment.TickCount64);
        }

        return (name, path, elevated);
    }

    private void TrimProcesses()
    {
        // Процессы живут дольше окна, но мёртвые записи копить незачем.
        foreach (var pair in _processes.OrderByDescending(p => p.Value.Stamp).Skip(MaxProcessEntries / 2).ToList())
            _processes.Remove(pair.Key);
    }

    /// <summary>Полный путь к исполняемому файлу процесса без .NET-обёртки модуля.</summary>
    private static string QueryProcessPath(int processId)
    {
        if (processId <= 0)
            return string.Empty;

        IntPtr handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero)
            return string.Empty;

        try
        {
            var buffer = new StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            return NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size)
                ? buffer.ToString()
                : string.Empty;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>
    /// Окно занимает весь монитор: прямоугольник совпадает с rcMonitor (допуск 2 px)
    /// либо окно без рамки (WS_POPUP без WS_CAPTION) занимает ≥ 95% площади монитора.
    /// </summary>
    private static bool IsFullscreenWindow(IntPtr window)
    {
        if (!NativeMethods.GetWindowRect(window, out var rect))
            return false;

        IntPtr monitor = NativeMethods.MonitorFromWindow(window, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return false;

        var info = new NativeMethods.MONITORINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            return false;

        var screen = info.rcMonitor;
        if (Math.Abs(rect.Left - screen.Left) <= FullscreenTolerancePx &&
            Math.Abs(rect.Top - screen.Top) <= FullscreenTolerancePx &&
            Math.Abs(rect.Right - screen.Right) <= FullscreenTolerancePx &&
            Math.Abs(rect.Bottom - screen.Bottom) <= FullscreenTolerancePx)
        {
            return true;
        }

        long style = NativeMethods.GetWindowStyle(window).ToInt64();
        bool borderlessPopup = (style & NativeMethods.WS_POPUP) != 0 &&
                               (style & NativeMethods.WS_CAPTION) == 0;
        if (!borderlessPopup)
            return false;

        long windowArea = Area(rect.Left, rect.Top, rect.Right, rect.Bottom);
        long screenArea = Area(screen.Left, screen.Top, screen.Right, screen.Bottom);
        return screenArea > 0 && windowArea >= screenArea * 95 / 100;
    }

    private static long Area(int left, int top, int right, int bottom)
    {
        long width = Math.Max(0, right - left);
        long height = Math.Max(0, bottom - top);
        return width * height;
    }

    /// <summary>
    /// Консольное окно текущего процесса или окно с консольным классом
    /// (включая Windows Terminal / ConPTY).
    /// </summary>
    private static bool IsConsoleWindow(IntPtr window)
    {
        if (window == IntPtr.Zero)
            return false;

        IntPtr console = NativeMethods.GetConsoleWindow();
        if (console != IntPtr.Zero && console == window)
            return true;

        string windowClass = NativeMethods.GetWindowClass(window);
        foreach (string candidate in ConsoleClasses)
        {
            if (string.Equals(windowClass, candidate, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private readonly record struct ProcessEntry(string Name, string Path, bool Elevated, long Stamp);
}
