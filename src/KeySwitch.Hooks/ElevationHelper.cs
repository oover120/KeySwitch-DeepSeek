using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace KeySwitch.Hooks;

/// <summary>
/// Права процесса и автозапуск. Автозапуск поддержан двумя способами:
/// ключ Run в HKCU (не требует прав администратора) и задача планировщика
/// (может запускать с повышенными правами, но создаётся только из админа).
/// </summary>
public static class ElevationHelper
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "KeySwitchDeepSeek";
    private const string TaskName = "KeySwitchDeepSeek";

    /// <summary>Запущен ли текущий процесс с повышенными правами (UAC).</summary>
    public static bool IsProcessElevated => IsElevatedProcess(Environment.ProcessId);

    /// <summary>
    /// Повышен ли указанный процесс. Если токен прочитать нельзя (отказано в доступе),
    /// считаем процесс повышенным: обычные процессы читаются без проблем.
    /// </summary>
    internal static bool IsElevatedProcess(int processId)
    {
        if (processId <= 0)
            return false;

        IntPtr processHandle;
        bool ownsProcessHandle;
        if (processId == Environment.ProcessId)
        {
            processHandle = NativeMethods.GetCurrentProcess();
            ownsProcessHandle = false;
        }
        else
        {
            processHandle = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            ownsProcessHandle = true;
        }

        if (processHandle == IntPtr.Zero)
            return Marshal.GetLastWin32Error() == NativeMethods.ERROR_ACCESS_DENIED;

        IntPtr token = IntPtr.Zero;
        try
        {
            if (!NativeMethods.OpenProcessToken(processHandle, NativeMethods.TOKEN_QUERY, out token))
                return Marshal.GetLastWin32Error() == NativeMethods.ERROR_ACCESS_DENIED;

            var elevation = default(NativeMethods.TOKEN_ELEVATION);
            if (!NativeMethods.GetTokenInformation(
                    token,
                    NativeMethods.TokenElevation,
                    out elevation,
                    Marshal.SizeOf<NativeMethods.TOKEN_ELEVATION>(),
                    out _))
            {
                return Marshal.GetLastWin32Error() == NativeMethods.ERROR_ACCESS_DENIED;
            }

            return elevation.TokenIsElevated != 0;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (token != IntPtr.Zero)
                NativeMethods.CloseHandle(token);
            if (ownsProcessHandle)
                NativeMethods.CloseHandle(processHandle);
        }
    }

    // ------------------------------------------------------------ HKCU Run

    /// <summary>Есть ли значение "KeySwitchDeepSeek" в HKCU\...\Run.</summary>
    public static bool IsRunKeyAutostartEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(RunValueName) is string value && value.Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Ставит или снимает автозапуск через ключ Run текущего пользователя.</summary>
    public static void SetRunKeyAutostart(bool enabled)
    {
        try
        {
            if (enabled)
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
                string path = ExecutablePath();
                if (path.Length == 0)
                    return;

                key.SetValue(RunValueName, $"\"{path}\"", RegistryValueKind.String);
            }
            else
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                key?.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception)
        {
            // Автозапуск — вспомогательная функция: сбой не должен ронять настройки.
        }
    }

    // ------------------------------------------------------------ Планировщик

    /// <summary>Зарегистрирована ли задача планировщика с именем "KeySwitchDeepSeek".</summary>
    public static bool IsScheduledAutostartEnabled()
    {
        return RunSchtasks($"/Query /TN \"{TaskName}\"") == 0;
    }

    /// <summary>
    /// Создаёт задачу на вход в систему. <paramref name="runAsAdmin"/> = true требует
    /// запуска из процесса с правами администратора (иначе задача не создастся).
    /// </summary>
    public static bool EnableScheduledAutostart(bool runAsAdmin)
    {
        string path = ExecutablePath();
        if (path.Length == 0)
            return false;

        string level = runAsAdmin ? "HIGHEST" : "LIMITED";
        string task = $"/TR \"\\\"{path}\\\"\"";

        return RunSchtasks(
            $"/Create /TN \"{TaskName}\" {task} /SC ONLOGON /RL {level} /F") == 0;
    }

    /// <summary>Удаляет задачу планировщика, если она есть.</summary>
    public static void DisableScheduledAutostart()
    {
        RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
    }

    // ------------------------------------------------------------ внутреннее

    private static string ExecutablePath()
    {
        try
        {
            string? path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
                return path;

            using Process process = Process.GetCurrentProcess();
            return process.MainModule?.FileName ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Вызов schtasks.exe без окна. Возвращает код возврата (−1, если запустить не удалось).
    /// Скрытый запуск обязателен: мигающая консоль при сохранении настроек выглядит как сбой.
    /// </summary>
    private static int RunSchtasks(string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"),
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using Process? process = Process.Start(startInfo);
            if (process is null)
                return -1;

            // Читаем потоки, чтобы процесс не встал на заполненном канале.
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            _ = output;
            _ = error;

            return process.WaitForExit(10_000) ? process.ExitCode : -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }
}
