using Earshot.Contracts;

namespace Earshot.Widget.EarPause;

// Safe mode for auto-pause: reads pass through, since a read changes nothing; TryPauseAsync and
// TryPlayAsync are refused, logged, and never reach the inner sessions. The whole pause path can run in
// safe mode with synthetic input and this decorator around it, the same shape SafeDecorators gives the
// three device controllers.
internal sealed class SafeMediaSessions : IMediaSessions
{
    public const string Message = "Safe mode: no device actions.";

    private readonly IMediaSessions _inner;
    private readonly ILog _log;

    public SafeMediaSessions(IMediaSessions inner, ILog log)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(log);
        _inner = inner;
        _log = log;
    }

    public event EventHandler<string>? PlaybackInfoChanged
    {
        add => _inner.PlaybackInfoChanged += value;
        remove => _inner.PlaybackInfoChanged -= value;
    }

    public bool ReportsChanges => _inner.ReportsChanges;

    public Task<IReadOnlyList<MediaSessionView>> ReadAsync(CancellationToken ct) => _inner.ReadAsync(ct);

    public Task<bool> TryPauseAsync(string sessionId, CancellationToken ct) => Refuse("pause");

    public Task<bool> TryPlayAsync(string sessionId, CancellationToken ct) => Refuse("play");

    private Task<bool> Refuse(string action)
    {
        _log.Warn(Message + " Refused: " + action + ".");
        return Task.FromResult(false);
    }
}
