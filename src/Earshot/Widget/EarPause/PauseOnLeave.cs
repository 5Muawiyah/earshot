using Earshot.App;
using Earshot.Contracts;
using Earshot.Tray;

namespace Earshot.Widget.EarPause;

// Pauses this PC's playback when the AirPods stop being this PC's output while this PC was playing to them, through
// the same Windows Media Controls pause auto-pause uses (SessionPause). It never resumes, never plays, and never acts
// on battery or in-ear data.
//
// "Playing to the AirPods" is known from an active audio session on the AirPods' render endpoint (IRenderActivity),
// sampled every SampleInterval while the AirPods are this PC's output. A leave Earshot did not start (the phone took
// them, they went out of range) is only seen once the endpoint has changed, and by then the endpoint's sessions are
// gone, so the decision uses the last sample taken before the change: a sample older than SampleFreshness, or one that
// says silent or unknown, pauses nothing. A leave Earshot starts itself (Disconnect, the hand-back at shut down, sleep
// and Exit, a fast switch) is decided beforehand from a fresh read (BeforeOwnLeaveAsync), and paused before the
// disconnect is sent, so the sound does not jump to the speakers.
//
// One decision is made for each stretch of time the AirPods are this PC's output (an episode), so a pause made
// before Earshot's own disconnect is not made again when the endpoint change follows.
//
// Threading: OnRender and BeforeOwnLeaveAsync are called on the UI thread; the sampling timer calls on a pool thread.
// All state is behind one lock, and no await or call out is made while it is held.
internal sealed class PauseOnLeave : IDisposable
{
    // Chosen, not measured, as every waiting budget here is. One second is fine enough that a change seen a moment
    // after the sound moved still finds a reading that says playing, and cheap: one read of session states.
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

    // Three intervals: a reading that survived one or two missed ticks is still a fair account of what this PC was
    // doing when the AirPods went; older than that says nothing about now.
    public static readonly TimeSpan SampleFreshness = TimeSpan.FromSeconds(3);

    // The most Earshot's own disconnect waits for the fresh read and the pause. A quarter of the disconnect's own
    // wait, and never more than this, so a slow media session can never take more than that from the disconnect and
    // block that keep the AirPods off this PC (see CapFor). The pause is not given up at the cap: it carries on and
    // writes its own line when it ends.
    public static readonly TimeSpan DefaultOwnLeaveCap = TimeSpan.FromMilliseconds(400);

    private readonly IMediaSessions _sessions;
    private readonly IRenderActivity _activity;
    private readonly Func<bool> _enabled;
    private readonly TimeProvider _time;
    private readonly ILog _log;
    private readonly object _gate = new();

    private RenderState _render = RenderState.Unknown;
    private Guid _container;
    private long _episode;
    private long _handledEpisode = -1;
    private Sample? _lastSample;
    private bool _sampling;
    private bool _unreadableNoted;
    private ITimer? _timer;
    private bool _disposed;

    private sealed record Sample(RenderActivityState State, DateTimeOffset At, long Episode);

    public PauseOnLeave(IMediaSessions sessions, IRenderActivity activity, Func<bool> enabled, TimeProvider time, ILog log)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        _sessions = sessions;
        _activity = activity;
        _enabled = enabled;
        _time = time;
        _log = log;
    }

    // The cap for a hand-back or a fast switch whose disconnect has disconnectWait to finish in.
    public static TimeSpan CapFor(TimeSpan disconnectWait) =>
        TimeSpan.FromTicks(Math.Min(DefaultOwnLeaveCap.Ticks, disconnectWait.Ticks / 4));

    // The render side of the AirPods as one snapshot showed it, seenAt when it was seen. Active to not active is a
    // leave. changeInFlight names an Earshot change running at that moment (a protection change drops the link and
    // brings it back), or is null: a leave during one is Earshot's own doing, not a leave, and pauses nothing.
    // Unknown (a read that failed) is not an observation and changes nothing. The task ends when the decision does.
    public Task OnRender(RenderState state, Guid container, DateTimeOffset seenAt, string? changeInFlight)
    {
        if (state == RenderState.Unknown)
        {
            return Task.CompletedTask;
        }

        Sample? sample;
        long episode;
        lock (_gate)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            RenderState before = _render;
            _render = state;
            if (state == RenderState.Active)
            {
                _container = container;
                if (before != RenderState.Active)
                {
                    _episode++;
                    _lastSample = null;
                    _unreadableNoted = false;
                    StartSamplingLocked();
                }

                return Task.CompletedTask;
            }

            if (before != RenderState.Active)
            {
                return Task.CompletedTask;
            }

            StopSamplingLocked();
            episode = _episode;
            sample = _lastSample;
            if (_handledEpisode == episode)
            {
                _log.Info(PauseOnLeaveText.AlreadyDecided(seenAt));
                return Task.CompletedTask;
            }

            _handledEpisode = episode;
        }

        return LeftAsync(seenAt, sample, changeInFlight);
    }

    // A leave Earshot is about to start: reads this PC's activity afresh and, when it was playing to the AirPods,
    // pauses before the disconnect. Waits at most cap. Never throws, and leaves nothing undecided: the decision for
    // this episode is made here, whatever it is. why names the leave in the log.
    public Task BeforeOwnLeaveAsync(string why, Guid container, TimeSpan cap, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(why);
        lock (_gate)
        {
            if (_disposed || _render != RenderState.Active || _handledEpisode == _episode)
            {
                return Task.CompletedTask;
            }

            _handledEpisode = _episode;
        }

        Task work = OwnLeaveAsync(why, container, ct);
        return WaitBoundedAsync(work, why, cap);
    }

    // Earshot's own disconnect ended. When the AirPods did not leave, the decision made before it is taken back, so
    // a leave that comes later in the same stretch (the phone taking them) is still decided.
    public void AfterOwnLeave(bool left)
    {
        if (left)
        {
            return;
        }

        lock (_gate)
        {
            if (_handledEpisode == _episode)
            {
                _handledEpisode = -1;
            }
        }
    }

    // One reading of this PC's activity, stored for the next leave. Public to the tests; the timer calls it.
    internal async Task SampleAsync()
    {
        Guid container;
        long episode;
        lock (_gate)
        {
            if (_disposed || _sampling || _render != RenderState.Active)
            {
                return;
            }

            _sampling = true;
            container = _container;
            episode = _episode;
        }

        RenderActivityReading reading;
        try
        {
            if (!_enabled())
            {
                return;
            }

            reading = await ReadSafeAsync(container, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _sampling = false;
            }
        }

        lock (_gate)
        {
            if (_disposed || _episode != episode || _render != RenderState.Active)
            {
                return;
            }

            _lastSample = new Sample(reading.State, _time.GetUtcNow(), episode);
            if (reading.State == RenderActivityState.Unknown && !_unreadableNoted)
            {
                _unreadableNoted = true;
                _log.Warn(PauseOnLeaveText.Prefix + "the AirPods audio could not be read, so a leave cannot be judged: " + Codes(reading) + ".");
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            StopSamplingLocked();
        }
    }

    private void StartSamplingLocked()
    {
        _timer ??= _time.CreateTimer(static state => _ = ((PauseOnLeave)state!).SampleAsync(), this, TimeSpan.Zero, SampleInterval);
    }

    private void StopSamplingLocked()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private async Task LeftAsync(DateTimeOffset seenAt, Sample? sample, string? changeInFlight)
    {
        try
        {
            if (!_enabled())
            {
                _log.Info(PauseOnLeaveText.LeftNotPaused(seenAt, PauseOnLeaveText.ReasonOff));
                return;
            }

            if (changeInFlight is not null)
            {
                _log.Info(PauseOnLeaveText.LeftNotPaused(seenAt, PauseOnLeaveText.ReasonChangeInFlight + " (" + changeInFlight + ")"));
                return;
            }

            if (sample is null)
            {
                _log.Info(PauseOnLeaveText.LeftNotPaused(seenAt, PauseOnLeaveText.ReasonNoReading));
                return;
            }

            TimeSpan age = seenAt - sample.At;
            if (age > SampleFreshness)
            {
                _log.Info(PauseOnLeaveText.LeftNotPaused(seenAt, PauseOnLeaveText.ReasonStaleReading(age)));
                return;
            }

            if (sample.State != RenderActivityState.Playing)
            {
                _log.Info(PauseOnLeaveText.LeftNotPaused(seenAt, PauseOnLeaveText.ReasonNotPlaying(sample.State)));
                return;
            }

            SessionPauseResult result = await SessionPause.PauseTheOnePlayingAsync(_sessions, CancellationToken.None).ConfigureAwait(false);
            TimeSpan after = _time.GetUtcNow() - seenAt;
            switch (result.Outcome)
            {
                case SessionPauseOutcome.Paused:
                    _log.Info(PauseOnLeaveText.LeftPaused(seenAt, result.AppId ?? "(unknown)", after, age));
                    break;
                case SessionPauseOutcome.NoneToPause:
                    _log.Info(PauseOnLeaveText.LeftNotPaused(seenAt, PauseOnLeaveText.ReasonNoneToPause));
                    break;
                case SessionPauseOutcome.Ambiguous:
                    _log.Info(PauseOnLeaveText.LeftNotPaused(seenAt, PauseOnLeaveText.ReasonAmbiguous(result.PlayingCount)));
                    break;
                default:
                    _log.Warn(PauseOnLeaveText.LeftNotPaused(seenAt, PauseOnLeaveText.ReasonRefused(result.AppId ?? "(unknown)")));
                    break;
            }
        }
        catch (Exception ex)
        {
            // The media-session boundary: never silent, and never lets a Windows Media Controls failure reach
            // the caller.
            _log.Error(PauseOnLeaveText.Prefix + "deciding a leave failed (" + ex.GetType().Name + ": " + ex.Message + ").", ex);
        }
    }

    private async Task OwnLeaveAsync(string why, Guid container, CancellationToken ct)
    {
        try
        {
            if (!_enabled())
            {
                _log.Info(PauseOnLeaveText.OwnNotPaused(why, PauseOnLeaveText.ReasonOff));
                return;
            }

            DateTimeOffset started = _time.GetUtcNow();
            RenderActivityReading reading = await ReadSafeAsync(container, ct).ConfigureAwait(false);
            RenderActivityState state = reading.State;
            string? unreadable = null;
            if (state == RenderActivityState.Unknown)
            {
                // The fresh read failed: the last sample stands in for it while it is still recent.
                Sample? sample;
                lock (_gate)
                {
                    sample = _lastSample;
                }

                if (sample is not null && _time.GetUtcNow() - sample.At <= SampleFreshness)
                {
                    state = sample.State;
                }
                else
                {
                    unreadable = PauseOnLeaveText.ReasonUnreadable(Codes(reading));
                }
            }

            if (state != RenderActivityState.Playing)
            {
                _log.Info(PauseOnLeaveText.OwnNotPaused(why, unreadable ?? PauseOnLeaveText.ReasonNotPlaying(state)));
                return;
            }

            SessionPauseResult result = await SessionPause.PauseTheOnePlayingAsync(_sessions, ct).ConfigureAwait(false);
            TimeSpan took = _time.GetUtcNow() - started;
            switch (result.Outcome)
            {
                case SessionPauseOutcome.Paused:
                    _log.Info(PauseOnLeaveText.OwnPaused(why, result.AppId ?? "(unknown)", took));
                    break;
                case SessionPauseOutcome.NoneToPause:
                    _log.Info(PauseOnLeaveText.OwnNotPaused(why, PauseOnLeaveText.ReasonNoneToPause));
                    break;
                case SessionPauseOutcome.Ambiguous:
                    _log.Info(PauseOnLeaveText.OwnNotPaused(why, PauseOnLeaveText.ReasonAmbiguous(result.PlayingCount)));
                    break;
                default:
                    _log.Warn(PauseOnLeaveText.OwnNotPaused(why, PauseOnLeaveText.ReasonRefused(result.AppId ?? "(unknown)")));
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.Info(PauseOnLeaveText.OwnNotPaused(why, "it was cancelled"));
        }
        catch (Exception ex)
        {
            _log.Error(PauseOnLeaveText.Prefix + "deciding an own leave failed (" + ex.GetType().Name + ": " + ex.Message + ").", ex);
        }
    }

    // Waits for the work up to cap. On the cap the caller goes on (the disconnect must not wait on a media session)
    // and the work carries on by itself: it writes its own line when it ends, so nothing it decides is lost.
    private async Task WaitBoundedAsync(Task work, string why, TimeSpan cap)
    {
        try
        {
            await work.WaitAsync(cap, _time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log.Info(PauseOnLeaveText.Prefix + "before Earshot lets the AirPods go (" + why + "), " +
                PauseOnLeaveText.ReasonTooSlow(cap) + ", so the disconnect goes ahead and the pause carries on.");
        }
    }

    private async Task<RenderActivityReading> ReadSafeAsync(Guid container, CancellationToken ct)
    {
        try
        {
            return await _activity.ReadAsync(container, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            StepOutcome step = StepOutcomes.FromHResult("render-activity", ex.HResult, ex.GetType().Name + ": " + ex.Message, ok: false);
            // An array, not a collection expression: the widget's at-rest scan allows only the types it names, and a
            // collection expression's compiler-made list is not one of them.
            return new RenderActivityReading(RenderActivityState.Unknown, 0, new[] { step });
        }
    }

    private static string Codes(RenderActivityReading reading)
    {
        List<string> parts = new();
        foreach (StepOutcome step in reading.Steps)
        {
            if (!step.Ok)
            {
                parts.Add(TrayReport.DescribeStep(step));
            }
        }

        return parts.Count == 0 ? "no code was given" : string.Join("; ", parts);
    }
}
