using Earshot.Contracts;
using Earshot.Widget.EarPause;

namespace Earshot.Tests.Widget;

// No Core Audio behind it. Answers what the test says this PC is rendering to the AirPods, and counts every read.
internal sealed class FakeRenderActivity : IRenderActivity
{
    public RenderActivityState State { get; set; } = RenderActivityState.Playing;

    public IReadOnlyList<StepOutcome> Steps { get; set; } = Array.Empty<StepOutcome>();

    // Replaces the answer for a test that needs a read that never comes back, or one that throws.
    public Func<Guid, CancellationToken, Task<RenderActivityReading>>? OnRead { get; set; }

    public int Reads { get; private set; }

    public List<string>? Trace { get; init; }

    public Task<RenderActivityReading> ReadAsync(Guid containerId, CancellationToken ct)
    {
        Reads++;
        Trace?.Add("read");
        if (OnRead is { } read)
        {
            return read(containerId, ct);
        }

        return Task.FromResult(new RenderActivityReading(State, State == RenderActivityState.Playing ? 1 : 0, Steps));
    }
}

// Windows Media Controls with a trace, so a test can say a pause came before a disconnect, and with a pause that can be
// held open.
internal sealed class TracingMediaSessions : IMediaSessions
{
    public List<MediaSessionView> Sessions { get; set; } = new();

    public bool PauseResult { get; set; } = true;

    public Func<Task<bool>>? OnPause { get; set; }

    public bool ReportsChanges => true;

    public List<string> PauseCalls { get; } = new();

    public List<string> PlayCalls { get; } = new();

    public List<string>? Trace { get; init; }

    public event EventHandler<string>? PlaybackInfoChanged
    {
        add { }
        remove { }
    }

    public static MediaSessionView Playing(string app) => new(app, MediaPlaybackState.Playing, IsPauseEnabled: true, IsPlayEnabled: true, app);

    public Task<IReadOnlyList<MediaSessionView>> ReadAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MediaSessionView>>(Sessions);

    public Task<bool> TryPauseAsync(string sessionId, CancellationToken ct)
    {
        PauseCalls.Add(sessionId);
        Trace?.Add("pause");
        return OnPause is { } pause ? pause() : Task.FromResult(PauseResult);
    }

    public Task<bool> TryPlayAsync(string sessionId, CancellationToken ct)
    {
        PlayCalls.Add(sessionId);
        Trace?.Add("play");
        return Task.FromResult(true);
    }
}
