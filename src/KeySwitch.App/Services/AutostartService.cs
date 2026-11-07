using KeySwitch.Data;
using KeySwitch.Hooks;

namespace KeySwitch.App.Services;

/// <summary>
/// Автозапуск: связка настройки <see cref="AppSettings.StartWithWindows"/>/<see cref="AppSettings.RunAsAdmin"/>
/// с реальным состоянием ОС. Два механизма:
/// * ключ HKCU\...\Run — обычный запуск без прав администратора;
/// * задача планировщика — умеет запускать с повышенными правами (RL HIGHEST), но создаётся
///   только из процесса с правами администратора.
/// Установщик тоже умеет создавать ключ Run, поэтому при старте автозапуск «усыновляется»:
/// если в ОС он есть, а в настройках выключен — включаем настройку, иначе она разошлась бы с ОС.
/// </summary>
public sealed class AutostartService
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly AppLogger _logger;

    public AutostartService(AppSettings settings, SettingsStore store, AppLogger logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Автозапуск реально присутствует в ОС (Run-ключ или задача планировщика).</summary>
    public bool IsAutostartPresent =>
        ElevationHelper.IsRunKeyAutostartEnabled() || ElevationHelper.IsScheduledAutostartEnabled();

    /// <summary>Права текущего процесса достаточны, чтобы создать задачу с повышением.</summary>
    public static bool CanElevate => ElevationHelper.IsProcessElevated;

    /// <summary>
    /// Приводит настройку и ОС к согласию. Вызывается из AppHost после загрузки настроек.
    /// </summary>
    public void Synchronize()
    {
        if (IsAutostartPresent && !_settings.StartWithWindows)
        {
            _settings.StartWithWindows = true;
            Save();
            _logger.Info("Автозапуск найден в системе — настройка включена.");
            return;
        }

        if (!IsAutostartPresent && _settings.StartWithWindows)
        {
            // Настройка есть, но ОС её не знает (например, exe перенесли) — восстанавливаем.
            if (!Apply())
                _logger.Warn("Настройка автозапуска включена, но восстановить его не удалось.");
        }
    }

    /// <summary>Включает/выключает автозапуск при входе в систему. true — удалось применить.</summary>
    public bool SetStartWithWindows(bool enabled)
    {
        _settings.StartWithWindows = enabled;
        Save();
        return Apply();
    }

    /// <summary>
    /// Переключает «запускать от администратора». При включении нужен уже повышенный процесс,
    /// иначе задача планировщика с RL HIGHEST не создастся — вернём false.
    /// </summary>
    public bool SetRunAsAdmin(bool runAsAdmin)
    {
        _settings.RunAsAdmin = runAsAdmin;
        Save();

        if (!_settings.StartWithWindows)
            return true; // режим запомним и применим, когда включат автозапуск

        if (runAsAdmin && !CanElevate)
        {
            _logger.Warn("«Запускать от администратора»: нужны права администратора — перезапустите приложение от имени администратора.");
            return false;
        }

        return Apply();
    }

    /// <summary>Приводит автозапуск ОС в соответствие текущим настройкам.</summary>
    public bool Apply()
    {
        try
        {
            if (!_settings.StartWithWindows)
            {
                ElevationHelper.SetRunKeyAutostart(false);
                if (ElevationHelper.IsScheduledAutostartEnabled())
                    ElevationHelper.DisableScheduledAutostart();
                return true;
            }

            if (_settings.RunAsAdmin)
            {
                // Задача с HIGHEST заменяет Run-ключ: иначе приложение стартовало бы дважды
                // или без прав.
                ElevationHelper.SetRunKeyAutostart(false);
                if (ElevationHelper.IsScheduledAutostartEnabled())
                    return true;

                return ElevationHelper.EnableScheduledAutostart(runAsAdmin: true);
            }

            if (ElevationHelper.IsScheduledAutostartEnabled())
                ElevationHelper.DisableScheduledAutostart();

            ElevationHelper.SetRunKeyAutostart(true);
            return ElevationHelper.IsRunKeyAutostartEnabled();
        }
        catch (Exception ex)
        {
            _logger.Error("Не удалось применить настройки автозапуска", ex);
            return false;
        }
    }

    private void Save()
    {
        try
        {
            _store.Save();
        }
        catch (Exception ex)
        {
            _logger.Error("Не удалось сохранить настройки автозапуска", ex);
        }
    }
}
