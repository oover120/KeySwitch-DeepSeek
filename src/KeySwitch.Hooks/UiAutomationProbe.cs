using System.Collections.Concurrent;
using System.Windows.Automation;

namespace KeySwitch.Hooks;

/// <summary>
/// UIA-проба активного элемента: поле пароля или обычное поле ввода.
/// Работает на выделенном MTA-потоке, потому что обращения к UIA — межпроцессные
/// вызовы на десятки миллисекунд, а хук блокировать нельзя. Ответ ждём не больше
/// <see cref="QueryTimeoutMs"/> мс; при таймауте отдаём закэшированное значение.
/// </summary>
public sealed class UiAutomationProbe
{
    private const int CacheTtlMs = 1000;
    private const int QueryTimeoutMs = 50;
    private const int MaxQueueLength = 16;
    private const int MaxCacheEntries = 64;

    /// <summary>
    /// Пауза после таймаута: если UIA не ответил за 30 мс, нет смысла тормозить
    /// каждое следующее нажатие теми же 30 мс — отдаём кэш и даём провайдеру очнуться.
    /// </summary>
    private const int TimeoutCooldownMs = 500;

    private readonly object _gate = new();
    private readonly Dictionary<IntPtr, CacheEntry> _cache = new();
    private readonly BlockingCollection<ProbeRequest> _queue =
        new(new ConcurrentQueue<ProbeRequest>(), MaxQueueLength);

    private Thread? _worker;
    private volatile bool _workerStuck;
    private long _cooldownUntil;

    /// <summary>true — фокус в поле пароля (ввод исправлять нельзя).</summary>
    public bool IsPasswordField(IntPtr window) => Query(window).IsPassword;

    /// <summary>true — фокус в поле ввода текста.</summary>
    public bool IsTextInput(IntPtr window) => Query(window).IsTextInput;

    // ------------------------------------------------------------ запросы

    private ProbeResult Query(IntPtr window)
    {
        if (window == IntPtr.Zero)
            return ProbeResult.Empty;

        lock (_gate)
        {
            if (_cache.TryGetValue(window, out CacheEntry entry))
            {
                if (Environment.TickCount64 - entry.Stamp < CacheTtlMs)
                    return entry.Value;
            }
        }

        // Недавно был таймаут UIA — не блокируем поток хука повторно.
        if (Environment.TickCount64 < Interlocked.Read(ref _cooldownUntil))
            return CachedOrEmpty(window);

        EnsureWorker();

        // Если поток UIA занят/завис, не копим запросы: отдаём то, что знаем.
        var request = new ProbeRequest(window);
        if (!_queue.TryAdd(request))
            return CachedOrEmpty(window);

        // Запрос не освобождаем принудительно: при таймауте его ещё может
        // обработать поток UIA, и Dispose превратился бы в гонку.
        if (request.Done.Wait(QueryTimeoutMs))
            return request.Result;

        Interlocked.Exchange(ref _cooldownUntil, Environment.TickCount64 + TimeoutCooldownMs);
        return CachedOrEmpty(window);
    }

    private ProbeResult CachedOrEmpty(IntPtr window)
    {
        lock (_gate)
            return _cache.TryGetValue(window, out CacheEntry entry) ? entry.Value : ProbeResult.Empty;
    }

    private void EnsureWorker()
    {
        if (_worker is not null || _workerStuck)
            return;

        lock (_gate)
        {
            if (_worker is not null)
                return;

            var thread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "KeySwitch.UiAutomationProbe"
            };

            // UIA-клиент живёт в MTA: STA заставил бы его обслуживать COM-сообщения.
            thread.SetApartmentState(ApartmentState.MTA);
            _worker = thread;
            thread.Start();
        }
    }

    private void WorkerLoop()
    {
        try
        {
            foreach (ProbeRequest request in _queue.GetConsumingEnumerable())
            {
                try
                {
                    ProbeResult result = ProbeCore(request.Window);
                    lock (_gate)
                    {
                        _cache[request.Window] = new CacheEntry(result, Environment.TickCount64);
                        if (_cache.Count > MaxCacheEntries)
                            TrimCache();
                    }

                    request.Result = result;
                }
                catch (Exception)
                {
                    request.Result = CachedOrEmpty(request.Window);
                }
                finally
                {
                    request.Done.Set();
                }
            }
        }
        catch (Exception)
        {
            // Поток UIA не должен ронять приложение: дальше отдаём только кэш.
            _workerStuck = true;
        }
    }

    private void TrimCache()
    {
        // Удаляем самые старые записи; актуальность здесь важнее полноты.
        foreach (var pair in _cache
                     .OrderByDescending(p => p.Value.Stamp)
                     .Skip(MaxCacheEntries / 2)
                     .ToList())
        {
            _cache.Remove(pair.Key);
        }
    }

    // ------------------------------------------------------------ UIA

    private static ProbeResult ProbeCore(IntPtr window)
    {
        // Возвращаемое значение — id потока окна; process id приходит в out-параметре.
        uint threadId = NativeMethods.GetWindowThreadProcessId(window, out uint processId);
        if (threadId == 0)
            return ProbeResult.Empty;

        AutomationElement? focused;
        try
        {
            focused = AutomationElement.FocusedElement;
        }
        catch (Exception)
        {
            return ProbeResult.Empty;
        }

        if (focused is null)
            return ProbeResult.Empty;

        try
        {
            // Фокус в другом процессе — значит наше окно не активно, гадать незачем.
            if (focused.Current.ProcessId != unchecked((int)processId))
                return ProbeResult.Empty;

            if (!IsFocusInWindow(focused, window))
                return ProbeResult.Empty;

            bool isPassword = focused.Current.IsPassword;
            bool isTextInput = !isPassword && IsTextInputElement(focused);
            return new ProbeResult(isPassword, isTextInput);
        }
        catch (ElementNotAvailableException)
        {
            return ProbeResult.Empty;
        }
        catch (Exception)
        {
            return ProbeResult.Empty;
        }
    }

    /// <summary>
    /// Проверка, что фокус действительно в нужном окне. Элемент может не отдавать
    /// handle (UIA-провайдеры браузеров), поэтому при отсутствии handle верим ProcessId.
    /// </summary>
    private static bool IsFocusInWindow(AutomationElement focused, IntPtr window)
    {
        try
        {
            int handle = focused.Current.NativeWindowHandle;
            if (handle == 0)
                return true;

            if (handle == window.ToInt32())
                return true;

            AutomationElement? current = focused;
            for (int depth = 0; depth < 12 && current is not null; depth++)
            {
                int parentHandle = current.Current.NativeWindowHandle;
                if (parentHandle == window.ToInt32())
                    return true;

                current = TreeWalker.ControlViewWalker.GetParent(current);
            }

            // Handle есть, но цепочка не дошла до окна — это другое окно процесса.
            return false;
        }
        catch (Exception)
        {
            // Не смогли проверить — не блокируем ввод, считаем, что фокус наш.
            return true;
        }
    }

    private static bool IsTextInputElement(AutomationElement element)
    {
        try
        {
            if (!element.Current.IsKeyboardFocusable)
                return false;

            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out _))
                return true;

            if (element.TryGetCurrentPattern(TextPattern.Pattern, out _))
                return true;

            ControlType controlType = element.Current.ControlType;
            return controlType == ControlType.Edit || controlType == ControlType.Document;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ------------------------------------------------------------ типы

    private readonly record struct ProbeResult(bool IsPassword, bool IsTextInput)
    {
        public static ProbeResult Empty => default;
    }

    private readonly record struct CacheEntry(ProbeResult Value, long Stamp);

    private sealed class ProbeRequest
    {
        public ProbeRequest(IntPtr window)
        {
            Window = window;
        }

        public IntPtr Window { get; }

        public ManualResetEventSlim Done { get; } = new(false);

        public ProbeResult Result { get; set; }
    }
}
