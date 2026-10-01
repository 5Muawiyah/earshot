using Earshot.Widget.EarPause;

namespace Earshot.Tests.Widget;

// No Windows Media Controls behind it. Records every call and lets a test choose the answer.
internal sealed class FakeMediaSessions : IMediaSessions
{
    public List<MediaSessionView> Sessions { get; set; } = new();

    public bool PauseResult { get; set; } = true;

    public bool PlayResult { get; set; } = true;

    // False stands in for a source that cannot say when playback changed (it raises no event whatever happens).
    public bool ReportsChanges { get; set; } = true;

    public List<string> PauseCalls { get; } = new();

    public List<string> PlayCalls { get; } = new();

    public event EventHandler<string>? PlaybackInfoChanged;

    public Task<IReadOnlyList<MediaSessionView>> ReadAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MediaSessionView>>(Sessions);

    public Task<bool> TryPauseAsync(string sessionId, CancellationToken ct)
    {
        PauseCalls.Add(sessionId);
        return Task.FromResult(PauseResult);
    }

    public Task<bool> TryPlayAsync(string sessionId, CancellationToken ct)
    {
        PlayCalls.Add(sessionId);
        return Task.FromResult(PlayResult);
    }

    public void RaisePlaybackInfoChanged(string sessionId) => PlaybackInfoChanged?.Invoke(this, sessionId);
}
