using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Tests.Streaming;
using Earshot.Widget;
using Earshot.Widget.EarPause;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// A media session list that behaves like the real one: a pause makes the session Paused, a play makes it Playing,
// and a test can press play or pause "by hand" and say a session's playback info changed. Every call is recorded.
internal sealed class FollowingMediaSessions : IMediaSessions
{
    public List<MediaSessionView> Sessions { get; } = new();

    public List<string> PauseCalls { get; } = new();

    public List<string> PlayCalls { get; } = new();

    public event EventHandler<string>? PlaybackInfoChanged;

    public static MediaSessionView Session(string id, MediaPlaybackState state) => new(id, state, true, true, id);

    public Task<IReadOnlyList<MediaSessionView>> ReadAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MediaSessionView>>(Sessions.ToArray());

    public Task<bool> TryPauseAsync(string sessionId, CancellationToken ct)
    {
        PauseCalls.Add(sessionId);
        SetState(sessionId, MediaPlaybackState.Paused);
        return Task.FromResult(true);
    }

    public Task<bool> TryPlayAsync(string sessionId, CancellationToken ct)
    {
        PlayCalls.Add(sessionId);
        SetState(sessionId, MediaPlaybackState.Playing);
        return Task.FromResult(true);
    }

    // What the person does with the player's own button, with Windows reporting the change.
    public void HandPlay(string sessionId)
    {
        SetState(sessionId, MediaPlaybackState.Playing);
        PlaybackInfoChanged?.Invoke(this, sessionId);
    }

    public void HandPause(string sessionId)
    {
        SetState(sessionId, MediaPlaybackState.Paused);
        PlaybackInfoChanged?.Invoke(this, sessionId);
    }

    // Windows reporting a change that is not one the person made: the late report of Earshot's own pause.
    public void Report(string sessionId) => PlaybackInfoChanged?.Invoke(this, sessionId);

    // A change nobody reported (the event the real source may not raise).
    public void SetState(string sessionId, MediaPlaybackState state)
    {
        int i = Sessions.FindIndex(s => s.SessionId == sessionId);
        if (i >= 0)
        {
            Sessions[i] = Sessions[i] with { PlaybackStatus = state };
        }
    }
}

// Synthetic broadcast messages with invented in-ear bits (bit 0 left, bit 1 right: the documented table sets none),
// through the real decoder, selector and WidgetStatusService into AutoPauseService, AutoPause and AutoResume, with a
// fake clock and a fake media session list. Nothing here is a real device value.
[TestClass]
public sealed class EarSequenceTests : IDisposable
{
    private static readonly Guid Container = Guid.NewGuid();

    private TempFolder _temp = null!;
    private CapturingLog _log = null!;
    private JsonSettingsStore _settings = null!;
    private TestTimeProvider _clock = null!;
    private FakeAdvertisementSource _source = null!;
    private StubMonitor _monitor = null!;
    private FollowingMediaSessions _sessions = null!;
    private WidgetStatusService _status = null!;
    private AutoPauseService _service = null!;

    // The device monitor's whole surface, with Current set by the test and SnapshotChanged raised by hand.
    private sealed class StubMonitor : IDeviceMonitor
    {
        public DeviceSnapshot Current { get; set; } = null!;

        public event EventHandler<DeviceSnapshotEventArgs>? SnapshotChanged;

        public bool WatchFailed => false;

        public void Start()
        {
        }

        public Task<DeviceSnapshot> RefreshAsync(CancellationToken ct = default) => Task.FromResult(Current);

        public void Dispose()
        {
        }

        public void Raise(DeviceSnapshot snapshot)
        {
            Current = snapshot;
            SnapshotChanged?.Invoke(this, new DeviceSnapshotEventArgs(snapshot));
        }
    }

    [TestInitialize]
    public void Setup()
    {
        _temp = new TempFolder();
        _log = new CapturingLog();
        _settings = new JsonSettingsStore(_temp.File("settings.json"), _log);
        _settings.Update(s => s.PinnedContainerId = Container);
        _clock = new TestTimeProvider();
        _source = new FakeAdvertisementSource();
        _monitor = new StubMonitor { Current = OnThisPc(connected: true) };
        _sessions = new FollowingMediaSessions();
    }

    public void Dispose()
    {
        _service?.Dispose();
        _status?.Dispose();
        _temp.Dispose();
    }

    private static ProximityDecodeTable InventedInEarBits() =>
        ProximityDecodeTable.Documented with { LeftInEarBit = 0, RightInEarBit = 1, InEarWhenSet = true };

    private DeviceSnapshot OnThisPc(bool connected)
    {
        var endpoint = new AudioEndpoint("ep1", EndpointFlow.Render, connected ? EndpointState.Active : EndpointState.Unplugged, "AirPods", Container);
        var model = new DeviceModel(Container, "AirPods", connected ? ConnectionState.Connected : ConnectionState.Disconnected, new[] { endpoint });
        return new DeviceSnapshot(model, new[] { model }, _clock.GetUtcNow())
        {
            ReadStatus = SnapshotReadStatus.Ok,
            Resolution = TargetResolution.Pinned,
            DefaultRenderContainerId = connected ? Container : Guid.Empty,
        };
    }

    private AutoPause NewAutoPause(IMediaSessions sessions) => new(sessions, () => _settings.Current.Widget.AutoPause, _log, _clock);

    // Builds the pipeline and gets the invented set chosen: the first choice waits for the paired model to have been
    // heard for two seconds and three messages, so three messages carrying no in-ear information go first.
    private void Start(ProximityDecodeTable table, IMediaSessions? sessions = null)
    {
        _status = new WidgetStatusService(
            () => _source, _settings, _monitor, () => null, _log, action => action(), _clock, new FakePairedModelSource(),
            handsFree: null, table, runInBackground: work => work());
        _service = new AutoPauseService(_status, _monitor, () => null, _settings, NewAutoPause(sessions ?? _sessions), _clock, _log);
        _status.Start();
        _monitor.Raise(OnThisPc(connected: true));
        for (int i = 0; i < 3; i++)
        {
            Send(left: false, right: false);
            Tick(1.1);
        }
    }

    private void Tick(double seconds) => _clock.Advance(TimeSpan.FromSeconds(seconds));

    // One message of the chosen set: status bit 0 is the left bud, bit 1 the right.
    private void Send(bool left, bool right)
    {
        byte status = (byte)(0x40 | (left ? 0x01 : 0) | (right ? 0x02 : 0));
        var message = new ProximityMessage(WidgetFixtures.ModelHigh, WidgetFixtures.ModelLow, status, 0x55, 0x05, 0x00, WidgetFixtures.Colour, 0x00);
        _source.Raise(new AdvertisementSample(
            ProximityParser.AppleCompanyId,
            WidgetFixtures.Proximity(message.ModelHigh, message.ModelLow, message.Status, message.BatteryA, message.BatteryB, message.Lid, message.Colour, message.Reserved),
            -60, _clock.GetUtcNow(), 1));
    }

    // The message sequence of the chosen set, one every 1.7 seconds (the cadence in use) for the given length.
    private void Hold(bool left, bool right, double seconds)
    {
        for (double t = 0; t < seconds; t += 1.7)
        {
            Send(left, right);
            Tick(1.7);
        }
    }

    private void StartPlaying(string id = "player.exe")
    {
        _sessions.Sessions.Add(FollowingMediaSessions.Session(id, MediaPlaybackState.Playing));
    }

    // ---- The two stages

    [TestMethod]
    public void ABudComingOutPausesAndItGoingBackWithinTheWindowResumesThatSession()
    {
        Start(InventedInEarBits());
        StartPlaying();
        Hold(left: true, right: true, 5);

        Send(left: false, right: true);
        string[] paused = { "player.exe" };
        CollectionAssert.AreEqual(paused, _sessions.PauseCalls);
        Assert.AreEqual(0, _sessions.PlayCalls.Count);

        Tick(1.7);
        Hold(left: false, right: true, 20);
        Send(left: true, right: true);

        string[] played = { "player.exe" };
        CollectionAssert.AreEqual(played, _sessions.PlayCalls);
        Assert.AreEqual(MediaPlaybackState.Playing, _sessions.Sessions[0].PlaybackStatus);
    }

    [TestMethod]
    public void ABudGoingBackAfterTheWindowDoesNotResume()
    {
        Start(InventedInEarBits());
        StartPlaying();
        Hold(left: true, right: true, 5);
        Send(left: false, right: true);
        Tick(WidgetTiming.ResumeWindow.TotalSeconds + 1);

        Send(left: true, right: true);

        Assert.AreEqual(1, _sessions.PauseCalls.Count);
        Assert.AreEqual(0, _sessions.PlayCalls.Count, "A bud back after the window never starts the sound, and not at a later reading either.");

        Hold(left: true, right: true, 10);
        Assert.AreEqual(0, _sessions.PlayCalls.Count);
    }

    [TestMethod]
    public void ABudGoingBackAtTheEdgeOfTheWindowResumes()
    {
        Start(InventedInEarBits());
        StartPlaying();
        Hold(left: true, right: true, 5);
        Send(left: false, right: true);
        Tick(WidgetTiming.ResumeWindow.TotalSeconds);

        Send(left: true, right: true);

        Assert.AreEqual(1, _sessions.PlayCalls.Count);
    }

    [TestMethod]
    public void APlayByHandInBetweenCancelsTheResume()
    {
        Start(InventedInEarBits());
        StartPlaying();
        Hold(left: true, right: true, 5);
        Send(left: false, right: true);
        Tick(WidgetTimingForTests.PastTheEcho);

        _sessions.HandPlay("player.exe");
        _sessions.HandPause("player.exe");
        Tick(5);
        Send(left: true, right: true);

        Assert.AreEqual(0, _sessions.PlayCalls.Count, "Played and paused by hand since Earshot's pause: it is the person's now.");
    }

    [TestMethod]
    public void APlayByHandThatWindowsDidNotReportStillCancelsTheResume()
    {
        Start(InventedInEarBits());
        StartPlaying();
        Hold(left: true, right: true, 5);
        Send(left: false, right: true);

        _sessions.SetState("player.exe", MediaPlaybackState.Playing);
        Tick(5);
        Send(left: true, right: true);

        Assert.AreEqual(0, _sessions.PlayCalls.Count, "The session does not read Paused any more, so Earshot does not play it.");
    }

    [TestMethod]
    public void APauseByHandOfAnotherSessionInBetweenCancelsTheResume()
    {
        Start(InventedInEarBits());
        StartPlaying();
        _sessions.Sessions.Add(FollowingMediaSessions.Session("other.exe", MediaPlaybackState.Stopped));
        Hold(left: true, right: true, 5);
        Send(left: false, right: true);
        Tick(2);

        _sessions.HandPause("other.exe");
        Tick(5);
        Send(left: true, right: true);

        Assert.AreEqual(0, _sessions.PlayCalls.Count);
    }

    [TestMethod]
    public void AnotherSessionStartingToPlayInBetweenCancelsTheResume()
    {
        Start(InventedInEarBits());
        StartPlaying();
        _sessions.Sessions.Add(FollowingMediaSessions.Session("other.exe", MediaPlaybackState.Paused));
        Hold(left: true, right: true, 5);
        Send(left: false, right: true);
        Tick(2);

        _sessions.HandPlay("other.exe");
        Tick(5);
        Send(left: true, right: true);

        Assert.AreEqual(0, _sessions.PlayCalls.Count);
    }

    [TestMethod]
    public void AnotherSessionPlayingAtTheReturnWithoutAnEventStillCancelsTheResume()
    {
        Start(InventedInEarBits());
        StartPlaying();
        _sessions.Sessions.Add(FollowingMediaSessions.Session("other.exe", MediaPlaybackState.Paused));
        Hold(left: true, right: true, 5);
        Send(left: false, right: true);

        _sessions.SetState("other.exe", MediaPlaybackState.Playing);
        Tick(5);
        Send(left: true, right: true);

        Assert.AreEqual(0, _sessions.PlayCalls.Count);
    }

    [TestMethod]
    public void TheAirPodsLeavingThisPcInBetweenCancelsTheResume()
    {
        Start(InventedInEarBits());
        StartPlaying();
        Hold(left: true, right: true, 5);
        Send(left: false, right: true);

        _monitor.Raise(OnThisPc(connected: false));
        Tick(5);
        _monitor.Raise(OnThisPc(connected: true));
        Send(left: true, right: true);

        Assert.AreEqual(0, _sessions.PlayCalls.Count, "They left this PC after the pause, so what was paused is not resumed even if they come back.");
    }

    [TestMethod]
    public void TheDefaultOutputMovingOffTheAirPodsInBetweenCancelsTheResume()
    {
        Start(InventedInEarBits());
        StartPlaying();
        Hold(left: true, right: true, 5);
        Send(left: false, right: true);

        _monitor.Raise(OnThisPc(connected: true) with { DefaultRenderContainerId = Guid.NewGuid() });
        Tick(5);
        _monitor.Raise(OnThisPc(connected: true));
        Send(left: true, right: true);

        Assert.AreEqual(0, _sessions.PlayCalls.Count);
    }

    [TestMethod]
    public void TurningTheSettingOffInBetweenCancelsTheResume()
    {
        Start(InventedInEarBits());
        StartPlaying();
        Hold(left: true, right: true, 5);
        Send(left: false, right: true);

        _settings.Update(s => s.Widget.AutoPause = false);
        Tick(5);
        Send(left: true, right: true);

        Assert.AreEqual(0, _sessions.PlayCalls.Count);
    }

    [TestMethod]
    public void APreviousStateOlderThanTheFreshWindowDoesNotPause()
    {
        Start(InventedInEarBits());
        StartPlaying();
        Send(left: true, right: true);
        Tick(WidgetTiming.EarFreshWindow.TotalSeconds + 1);

        Send(left: false, right: true);

        Assert.AreEqual(0, _sessions.PauseCalls.Count, "An 11 second old 'in' must not pair with a new 'out'.");
    }

    [TestMethod]
    public void TwoSessionsPlayingPauseNothingAndResumeNothing()
    {
        Start(InventedInEarBits());
        StartPlaying("one.exe");
        StartPlaying("two.exe");
        Hold(left: true, right: true, 5);

        Send(left: false, right: true);
        Tick(3);
        Send(left: true, right: true);

        Assert.AreEqual(0, _sessions.PauseCalls.Count);
        Assert.AreEqual(0, _sessions.PlayCalls.Count);
    }

    [TestMethod]
    public void OnlyTheSessionEarshotPausedIsResumed()
    {
        Start(InventedInEarBits());
        _sessions.Sessions.Add(FollowingMediaSessions.Session("idle.exe", MediaPlaybackState.Paused));
        StartPlaying("player.exe");
        _sessions.Sessions.Add(FollowingMediaSessions.Session("stopped.exe", MediaPlaybackState.Stopped));
        Hold(left: true, right: true, 5);
        Send(left: false, right: true);
        Tick(5);

        Send(left: true, right: true);

        string[] played = { "player.exe" };
        CollectionAssert.AreEqual(played, _sessions.PlayCalls);
    }

    [TestMethod]
    public void NothingPlayingMeansNothingToResume()
    {
        Start(InventedInEarBits());
        _sessions.Sessions.Add(FollowingMediaSessions.Session("idle.exe", MediaPlaybackState.Paused));
        Hold(left: true, right: true, 5);

        Send(left: false, right: true);
        Tick(5);
        Send(left: true, right: true);

        Assert.AreEqual(0, _sessions.PauseCalls.Count);
        Assert.AreEqual(0, _sessions.PlayCalls.Count, "A session already paused by hand is never resumed by Earshot.");
    }

    [TestMethod]
    public void ResumeWaitsForEveryBudThatWasInAndResumesOnce()
    {
        Start(InventedInEarBits());
        StartPlaying();
        Hold(left: true, right: true, 5);

        Send(left: false, right: true);
        Tick(1.7);
        Send(left: false, right: false);
        Tick(1.7);
        Send(left: true, right: false);
        Assert.AreEqual(0, _sessions.PlayCalls.Count, "One bud is back and the other is not.");

        Tick(1.7);
        Send(left: true, right: true);
        Assert.AreEqual(1, _sessions.PlayCalls.Count);

        Hold(left: true, right: true, 10);
        Assert.AreEqual(1, _sessions.PlayCalls.Count, "Resumed once, never again.");
        Assert.AreEqual(1, _sessions.PauseCalls.Count);
    }

    [TestMethod]
    public void ABudThatWasNeverInTheEarIsNotWaitedFor()
    {
        Start(InventedInEarBits());
        StartPlaying();
        Hold(left: true, right: false, 5);

        Send(left: false, right: false);
        Tick(1.7);
        Send(left: true, right: false);

        Assert.AreEqual(1, _sessions.PlayCalls.Count, "The right bud was out before the pause, so only the left has to be back.");
    }

    [TestMethod]
    public void AnEchoOfEarshotsOwnPauseDoesNotCancelTheResume()
    {
        Start(InventedInEarBits());
        StartPlaying();
        Hold(left: true, right: true, 5);
        Send(left: false, right: true);

        Tick(0.4);
        _sessions.Report("player.exe");
        Tick(5);
        Send(left: true, right: true);

        Assert.AreEqual(1, _sessions.PlayCalls.Count);
    }

    [TestMethod]
    public void ALaterChangeToThePausedSessionCancelsTheResume()
    {
        Start(InventedInEarBits());
        StartPlaying();
        Hold(left: true, right: true, 5);
        Send(left: false, right: true);

        Tick(WidgetTimingForTests.PastTheEcho);
        _sessions.Report("player.exe");
        Send(left: true, right: true);

        Assert.AreEqual(0, _sessions.PlayCalls.Count);
    }

    // ---- Safe mode, and a table with no in-ear bits (production today)

    [TestMethod]
    public void SafeModeRefusesThePauseSoNothingIsRememberedOrResumed()
    {
        var inner = new FollowingMediaSessions();
        inner.Sessions.Add(FollowingMediaSessions.Session("player.exe", MediaPlaybackState.Playing));
        Start(InventedInEarBits(), new SafeMediaSessions(inner, _log));
        Hold(left: true, right: true, 5);

        Send(left: false, right: true);
        Tick(5);
        Send(left: true, right: true);

        Assert.AreEqual(0, inner.PauseCalls.Count);
        Assert.AreEqual(0, inner.PlayCalls.Count);
        Assert.IsTrue(_log.Has(LogLevel.Warn, "Safe mode"));
    }

    [TestMethod]
    public void SafeModeRefusesAResumeToo()
    {
        var inner = new FollowingMediaSessions();
        inner.Sessions.Add(FollowingMediaSessions.Session("player.exe", MediaPlaybackState.Paused));
        var safe = new SafeMediaSessions(inner, _log);

        Assert.IsFalse(safe.TryPlayAsync("player.exe", CancellationToken.None).GetAwaiter().GetResult());

        Assert.AreEqual(0, inner.PlayCalls.Count);
        Assert.IsTrue(_log.Has(LogLevel.Warn, "Refused: play"));
    }

    [TestMethod]
    public void WithTheDocumentedTableNothingPausesOrResumesBecauseNoInEarBitIsDecoded()
    {
        Start(ProximityDecodeTable.Documented);
        StartPlaying();

        // Every status byte the invented table would read as an in-ear change, through the table that decodes none.
        Hold(left: true, right: true, 5);
        Send(left: false, right: true);
        Tick(5);
        Send(left: true, right: true);
        Send(left: false, right: false);

        Assert.IsFalse(_status.Current.AutoPauseAvailable);
        Assert.AreEqual(0, _sessions.PauseCalls.Count);
        Assert.AreEqual(0, _sessions.PlayCalls.Count);
    }

    // The ear windows are WidgetTiming's; the tests only need a moment past the echo of a pause.
    private static class WidgetTimingForTests
    {
        public static double PastTheEcho => WidgetTiming.OwnPauseEchoWindow.TotalSeconds + 0.5;
    }
}
