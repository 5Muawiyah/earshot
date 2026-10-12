using Earshot.Contracts;

namespace Earshot.Infra;

// Watches the settings file and makes a running tray take up a change made by another program: the settings command, or an edit
// by hand. A change event is held for a short settle time (a save is a write to a temporary file and a replace, which raises
// several events), then the store loads the file again only if it holds something the store did not write itself. The store
// raises Changed, which is the path a change from the settings page already takes.
// https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher
internal sealed class SettingsFileWatcher : IDisposable
{
    internal static readonly TimeSpan DefaultSettle = TimeSpan.FromMilliseconds(250);

    private readonly JsonSettingsStore _store;
    private readonly ILog _log;
    private readonly FileSystemWatcher _watcher;
    private readonly System.Threading.Timer _settle;
    private readonly TimeSpan _delay;
    private bool _disposed;

    public SettingsFileWatcher(JsonSettingsStore store, ILog log, TimeSpan? settle = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(log);
        _store = store;
        _log = log;
        _delay = settle ?? DefaultSettle;
        string folder = Path.GetDirectoryName(store.FilePath)!;
        Directory.CreateDirectory(folder);
        _settle = new System.Threading.Timer(_ => Reload(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
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

    private void Reload()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _store.ReloadIfFileChanged();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            // A handler of Changed that failed, or a file that could not be used: the tray keeps what it has.
            _log.Error("Settings changed on disk but could not be taken up.", ex);
        }
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
