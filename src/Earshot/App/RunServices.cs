using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Infra;

namespace Earshot.App;

// The ServiceRegistry for a console run mode (probe, diag), built the way the tray builds it but
// only when a target first asks for it. There is no UI thread in these modes, so UiPost runs the
// action on the calling thread. Disposes the monitor and the audio worker at the end.
//
// Settings are opened read-only: probe must change nothing, so a missing or unusable settings file
// is neither created, saved nor moved aside here.
internal sealed class RunServices : IDisposable
{
    private readonly Paths _paths;
    private readonly ILog _log;
    private ServiceRegistry? _registry;

    public RunServices(Paths paths, ILog log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);
        _paths = paths;
        _log = log;
    }

    public bool IsBuilt => _registry is not null;

    public ServiceRegistry Get()
    {
        if (_registry is null)
        {
            var settings = new JsonSettingsStore(_paths.SettingsFile, _log, readOnly: true);
            _registry = CompositionRoot.Build(_log, settings, static action => action(), _paths.IsSafeMode);
        }

        return _registry;
    }

    public void Dispose()
    {
        if (_registry is null)
        {
            return;
        }

        _registry.Monitor.Dispose();
        if (_registry.Worker is { } worker)
        {
            worker.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        (_registry.SystemWorker as IDisposable)?.Dispose();

        _registry = null;
    }
}
