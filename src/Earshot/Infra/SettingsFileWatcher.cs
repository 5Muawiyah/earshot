using Earshot.Contracts;

namespace Earshot.Infra;

// Watches the settings file and makes a running tray take up a change made by another program: the settings command, or an edit
// by hand. A change event is held for a short settle time (a save is a write to a temporary file and a replace, which raises
// several events), then the store loads the file again only if it holds something the store did not write itself. The store
// raises Changed, which is the path a change from the settings page already takes. The reload is posted to the UI thread, where
// that path belongs. A file that could not be read (its writer still holds it) is tried again a few times.
// https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher
internal sealed class SettingsFileWatcher : IDisposable
{
    internal static readonly TimeSpan DefaultSettle = TimeSpan.FromMilliseconds(250);

    // How many more times a file that could not be read is tried, one settle time apart.
    internal const int MaxRetries = 8;

    private readonly JsonSettingsStore _store;
    private readonly ILog _log;
    private readonly FileSystemWatcher _watcher;
    private readonly System.Threading.Timer _settle;
    private readonly TimeSpan _delay;
    private readonly Action<Action> _uiPost;
    private int _retries;
    private bool _disposed;

    // uiPost runs an action on the thread the store's subscribers belong to (the tray's registry.UiPost).
    public SettingsFileWatcher(JsonSettingsStore store, ILog log, Action<Action> uiPost, TimeSpan? settle = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(uiPost);
        _store = store;
        _log = log;
        _uiPost = uiPost;
        _delay = settle ?? DefaultSettle;
        string folder = Path.GetDirectoryName(store.FilePath)!;
        Directory.CreateDirectory(folder);
        _settle = new System.Threading.Timer(_ => PostReload(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _watcher = new FileSystemWatcher(folder, Path.GetFileName(store.FilePath))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = false,
        };
        _watcher.Changed += OnEvent;
        _watcher.Created += OnEvent;
        _watcher.Renamed += OnEvent;
        _watcher.Error += (_, e) => _log.Warn("The settings file watcher failed: " + e.GetException().Message);
        _watcher.EnableRaisingEvents = true;
    }

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        if (!_disposed)
        {
            _settle.Change(_delay, Timeout.InfiniteTimeSpan);
        }
    }

    // The settle timer fires on a thread-pool thread. The store raises Changed from wherever the reload runs, and its subscribers
    // (the block coordinator, the low battery latch, the card) belong to the UI thread, so the reload is posted to it.
    private void PostReload() => _uiPost(Reload);

    private void Reload()
    {
        if (_disposed)
        {
            return;
        }

        SettingsReload result;
        try
        {
            result = _store.ReloadIfFileChanged();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            // A handler of Changed that failed, or a file that could not be used: the tray keeps what it has.
            _log.Error("Settings changed on disk but could not be taken up.", ex);
            return;
        }

        if (result != SettingsReload.TryAgain)
        {
            _retries = 0;
            return;
        }

        // The writer may still hold the file. No further change event is raised when it lets go, so ask again a few times.
        if (++_retries > MaxRetries)
        {
            _log.Warn("The settings file changed on disk but could not be read after " + MaxRetries.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                      " more tries, so the values in use are kept until it changes again.");
            _retries = 0;
            return;
        }

        _settle.Change(_delay, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _settle.Dispose();
    }
}
