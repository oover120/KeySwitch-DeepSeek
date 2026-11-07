using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using KeySwitch.Data;

namespace KeySwitch.App.Services;

/// <summary>
/// Звуковые эффекты. WPF MediaPlayer умеет громкость на экземпляр (SoundPlayer — нет,
/// а waveOutSetVolume меняет громкость всей системы), поэтому используется MediaPlayer.
/// Экземпляры кэшируются по пути: MediaPlayer.Open асинхронен, повторный Play не переоткрывает файл.
/// </summary>
public sealed class SoundService : IDisposable
{
    private readonly AppLogger _logger;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, MediaPlayer> _players = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private double _volume = 0.7;
    private bool _disposed;

    public SoundService(AppLogger logger)
    {
        _logger = logger;
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        Shared = this;
    }

    /// <summary>Экземпляр для кнопки «Прослушать» в окне настроек (создаётся при старте приложения).</summary>
    public static SoundService? Shared { get; private set; }

    /// <summary>Громкость 0..1 (в настройках хранится как 0..1).</summary>
    public void SetVolume(double volume) => _volume = Math.Clamp(volume, 0.0, 1.0);

    /// <summary>Проигрывает файл, если он существует. Тихо игнорирует пустой путь.</summary>
    public void Play(string? path, double volume)
    {
        SetVolume(volume);
        Play(path);
    }

    public void Play(string? path)
    {
        if (_disposed || string.IsNullOrWhiteSpace(path))
            return;

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            _logger.Debug($"звук: путь '{path}' некорректен: {ex.Message}");
            return;
        }

        lock (_gate)
        {
            if (_failed.Contains(full) || !File.Exists(full))
                return;
        }

        PlayCore(full);
    }

    /// <summary>Предпрослушивание с текущей громкостью (кнопка «Прослушать»).</summary>
    public void Preview(string? path) => Play(path);

    private void PlayCore(string fullPath)
    {
        if (_disposed)
            return;

        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(() => PlayCore(fullPath)));
            return;
        }

        try
        {
            if (_volume <= 0.001)
                return;

            MediaPlayer player;
            lock (_gate)
            {
                if (!_players.TryGetValue(fullPath, out player!))
                {
                    player = new MediaPlayer();
                    player.MediaFailed += (_, args) =>
                    {
                        lock (_gate)
                        {
                            _failed.Add(fullPath);
                        }

                        _logger.Warn($"звук '{fullPath}' не воспроизводится: {args.ErrorException?.Message}");
                    };
                    player.Open(new Uri(fullPath, UriKind.Absolute));
                    _players[fullPath] = player;
                }
            }

            player.Volume = _volume;
            player.Position = TimeSpan.Zero;
            player.Play();
        }
        catch (Exception ex)
        {
            _logger.Warn($"звук: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_dispatcher.CheckAccess())
            CloseAll();
        else
            _dispatcher.BeginInvoke(new Action(CloseAll));

        if (ReferenceEquals(Shared, this))
            Shared = null;
    }

    private void CloseAll()
    {
        lock (_gate)
        {
            foreach (var player in _players.Values)
            {
                try
                {
                    player.Stop();
                    player.Close();
                }
                catch
                {
                    // ignore
                }
            }

            _players.Clear();
        }
    }
}
