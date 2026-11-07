using System.Text;

namespace KeySwitch.Data;

/// <summary>
/// Простой файловый логгер с ротацией. Пишет только технические события:
/// НИКОГДА не передавай сюда текст набора, содержимое буфера или иной пользовательский
/// ввод — уровень данных не должен превращаться в кейлоггер даже случайно.
/// </summary>
public sealed class AppLogger
{
    /// <summary>Порог ротации: 2 МБ, держим один бэкап <c>keyswitch.log.1</c>.</summary>
    public const long MaxFileSize = 2 * 1024 * 1024;

    private readonly object _lock = new();
    private readonly AppPaths _paths;
    private readonly bool _safeMode;
    private StreamWriter? _writer;
    private long _bytesWritten;

    public AppLogger(AppPaths paths, bool safeMode = false)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _safeMode = safeMode;
        Verbose = IsVerboseRequested();
    }

    /// <summary>Включать ли Debug-сообщения (по умолчанию — только если задан KEYSWITCH_DEBUG=1).</summary>
    public bool Verbose { get; set; }

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message) => Write("WARN", message, null);

    public void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    public void Debug(string message)
    {
        if (Verbose)
            Write("DEBUG", message, null);
    }

    public void Flush()
    {
        lock (_lock)
        {
            try
            {
                _writer?.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Файл мог быть удалён/занят — логгер не имеет права падать.
            }
        }
    }

    private void Write(string level, string message, Exception? ex)
    {
        // В безопасном режиме не трогаем диск лишний раз: только предупреждения и ошибки.
        if (_safeMode && level is "INFO" or "DEBUG")
            return;

        if (string.IsNullOrEmpty(message) && ex is null)
            return;

        var line = new StringBuilder(160);
        line.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        line.Append(' ').Append(level).Append(' ').Append(message ?? string.Empty);
        if (ex is not null)
            line.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);

        lock (_lock)
        {
            try
            {
                var writer = GetWriter();
                if (writer is null)
                    return;

                writer.WriteLine(line.ToString());
                writer.Flush();
                _bytesWritten += Encoding.UTF8.GetByteCount(line.ToString()) + 2;

                if (_bytesWritten > MaxFileSize)
                    Rotate();
            }
            catch (Exception logEx) when (logEx is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // Логирование не должно ломать приложение.
                CloseWriter();
            }
        }
    }

    private StreamWriter? GetWriter()
    {
        if (_writer is not null)
            return _writer;

        Directory.CreateDirectory(_paths.LogsDir);
        _bytesWritten = File.Exists(_paths.LogFile) ? new FileInfo(_paths.LogFile).Length : 0;
        var stream = new FileStream(_paths.LogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
        return _writer;
    }

    private void Rotate()
    {
        CloseWriter();

        var backup = _paths.LogFile + ".1";
        try
        {
            if (File.Exists(_paths.LogFile))
                File.Move(_paths.LogFile, backup, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Не смогли переименовать — просто продолжаем писать в тот же файл,
            // размер проверится на следующей записи.
        }

        _bytesWritten = 0;
    }

    private void CloseWriter()
    {
        try
        {
            _writer?.Flush();
            _writer?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
        finally
        {
            _writer = null;
        }
    }

    private static bool IsVerboseRequested()
    {
        var value = Environment.GetEnvironmentVariable("KEYSWITCH_DEBUG");
        return !string.IsNullOrWhiteSpace(value)
               && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
    }
}
