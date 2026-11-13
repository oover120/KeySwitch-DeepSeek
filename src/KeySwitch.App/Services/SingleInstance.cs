using System.Threading;

namespace KeySwitch.App.Services;

/// <summary>
/// Один экземпляр приложения на пользовательскую сессию.
/// Основной механизм — именованный Mutex. Дополнительно создаётся событие активации:
/// второй экземпляр не просто выходит, а просит уже запущенный показать настройки.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string BaseMutexName = @"KeySwitchDeepSeek.SingleInstance.{0}";
    private const string BaseEventName = @"KeySwitchDeepSeek.Activate.{0}";
    private const string BaseTakeoverName = @"KeySwitchDeepSeek.Takeover.{0}";

    // Global\ даёт единственный экземпляр на всю машину, но создание объекта в глобальном
    // пространстве имён требует SeCreateGlobalPrivilege. У обычного пользователя его нет,
    // поэтому используется откат на Local\ (один экземпляр на сессию).
    private static readonly string[] Scopes = { "Global", "Local" };

    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private EventWaitHandle? _takeoverEvent;
    private RegisteredWaitHandle? _registeredWait;
    private RegisteredWaitHandle? _registeredTakeoverWait;
    private bool _disposed;

    /// <summary>Запрошена активация (пользователь запустил второй экземпляр).</summary>
    public event EventHandler? ActivationRequested;

    /// <summary>
    /// Запрошена передача управления: запускается повышенный экземпляр, и текущему
    /// нужно освободить мьютекс и завершиться. Иначе «Запуск от имени администратора»
    /// приводил бы к тому, что админский процесс молча выходит, а старый остаётся.
    /// </summary>
    public event EventHandler? TakeoverRequested;

    /// <summary>Захватывает мьютекс. false — экземпляр уже запущен.</summary>
    public bool TryAcquire()
    {
        foreach (var scope in Scopes)
        {
            foreach (var global in new[] { true, false })
            {
                var name = string.Format(BaseMutexName, global ? scope : "Session");
                try
                {
                    var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
                    if (!createdNew)
                    {
                        // Мьютекс уже существует — второй экземпляр.
                        mutex.Dispose();
                        _mutex = null;
                        return false;
                    }

                    _mutex = mutex;
                    CreateActivationEvent(global);
                    CreateTakeoverEvent(global);
                    return true;
                }
                catch (UnauthorizedAccessException)
                {
                    // Нет прав на Global\ — пробуем следующий вариант имени.
                }
                catch (Exception)
                {
                    // Сбой именованного объекта не должен мешать запуску приложения —
                    // работаем без защиты от второго экземпляра.
                    _mutex = null;
                    return true;
                }
            }
        }

        return true;
    }

    /// <summary>Начинает ожидание запроса активации от второго экземпляра.</summary>
    public void StartListening()
    {
        if (_activateEvent != null)
        {
            _registeredWait = ThreadPool.RegisterWaitForSingleObject(
                _activateEvent,
                (_, _) => ActivationRequested?.Invoke(this, EventArgs.Empty),
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }

        if (_takeoverEvent != null)
        {
            _registeredTakeoverWait = ThreadPool.RegisterWaitForSingleObject(
                _takeoverEvent,
                (_, _) => TakeoverRequested?.Invoke(this, EventArgs.Empty),
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }
    }

    /// <summary>
    /// Захватывает мьютекс, при необходимости отобрав его у текущего экземпляра.
    /// Используется только при перезапуске с повышением прав: новый процесс просит
    /// старый завершиться и ждёт освобождения мьютекса.
    /// </summary>
    public bool TryAcquireWithTakeover(TimeSpan timeout)
    {
        if (TryAcquire())
            return true;

        SignalTakeover();

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(100);
            if (TryAcquire())
                return true;
        }

        return false;
    }

    /// <summary>Второй экземпляр: просит уже запущенный передать управление и завершиться.</summary>
    public static void SignalTakeover()
    {
        Signal(BaseTakeoverName);
    }

    private static void Signal(string baseName)
    {
        foreach (var scope in Scopes)
        {
            foreach (var global in new[] { true, false })
            {
                var name = string.Format(baseName, global ? scope : "Session");
                try
                {
                    if (EventWaitHandle.TryOpenExisting(name, out var handle))
                    {
                        using (handle)
                        {
                            handle.Set();
                        }

                        return;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (Exception)
                {
                }
            }
        }
    }

    /// <summary>Второй экземпляр: сигнализирует уже запущенному приложению.</summary>
    public static void SignalExistingInstance()
    {
        Signal(BaseEventName);
    }

    private void CreateTakeoverEvent(bool global)
    {
        foreach (var scope in Scopes)
        {
            var name = string.Format(BaseTakeoverName, global ? scope : "Session");
            try
            {
                _takeoverEvent = new EventWaitHandle(false, EventResetMode.AutoReset, name);
                return;
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (Exception)
            {
            }
        }
    }

    private void CreateActivationEvent(bool global)
    {
        foreach (var scope in Scopes)
        {
            var name = string.Format(BaseEventName, global ? scope : "Session");
            try
            {
                _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, name);
                return;
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (Exception)
            {
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            _registeredWait?.Unregister(null);
            _registeredTakeoverWait?.Unregister(null);
        }
        catch
        {
            // ignore
        }

        _takeoverEvent?.Dispose();
        _takeoverEvent = null;

        _activateEvent?.Dispose();
        _activateEvent = null;

        if (_mutex != null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // Мьютекс не принадлежал нам — не страшно.
            }

            _mutex.Dispose();
            _mutex = null;
        }
    }
}
