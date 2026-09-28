using Earshot.App;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Widget;
using Earshot.Widget.EarPause;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// Step 6 of the widget plan: wiring AutoPause end to end from IWidgetStatus.OwnedReadingApplied, not
// AutoPause's own decision logic (AutoPauseTests already covers that) and not WidgetStatusService's own
// contract that OwnedReadingApplied fires only for an owned reading (WidgetStatusServiceTests already pins
// that from the previous pass).
[TestClass]
public sealed class AutoPauseServiceTests : IDisposable
{
    // IWidgetStatus's whole public surface, with only OwnedReadingApplied actually driven and Current
    // settable, the same shape LowBatteryAlertServiceTests' own FakeWidgetStatus uses for the same reason:
    // this proves the WIRING, not IWidgetStatus or WidgetStatusService themselves.
    private sealed class FakeWidgetStatus : IWidgetStatus
    {
        public WidgetSnapshot Current { get; set; } = WidgetSnapshot.Empty(WidgetWatcherState.Started, claimExists: true);

        public bool ClaimAvailable => false;

        public event EventHandler? Changed;

        public event EventHandler<CaseOpenedEventArgs>? CaseOpened;

        public event EventHandler<OwnedReadingEventArgs>? OwnedReadingApplied;

        public Task<ClaimOutcome> ClaimAsync(CancellationToken ct) => throw new NotSupportedException();

        public void ForgetClaim() => throw new NotSupportedException();

        public Task RefreshAsync() => Task.CompletedTask;

        public void Raise(DecodedReading reading, DateTimeOffset at) =>
            OwnedReadingApplied?.Invoke(this, new OwnedReadingEventArgs(reading, at));

        // Unused by this test class; kept so the type fully implements the interface without a warning.
        internal void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

        internal void RaiseCaseOpened(DateTimeOffset at) => CaseOpened?.Invoke(this, new CaseOpenedEventArgs(at));
    }

    // IDeviceMonitor's whole surface, with only Current read. SnapshotChanged is never raised: nothing
    // under test subscribes to it.
    private sealed class FakeDeviceMonitor : IDeviceMonitor
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

        // Kept only so the field-like event above counts as used; nothing under test subscribes to it.
        internal void RaiseSnapshotChanged(DeviceSnapshotEventArgs e) => SnapshotChanged?.Invoke(this, e);
    }

    // Throws synchronously from ReadAsync, the media-session boundary AutoPause.ApplyAsync reaches first:
    // proves an exception crossing that boundary is caught and logged by AutoPauseService, never left to
    // crash the fire-and-forget handler. A dedicated fake, not a new field on the shared FakeMediaSessions
    // (which AutoPauseTests.cs also uses and whose assertions must not move).
    private sealed class ThrowingMediaSessions : IMediaSessions
    {
        public event EventHandler<string>? PlaybackInfoChanged;

        public Task<IReadOnlyList<MediaSessionView>> ReadAsync(CancellationToken ct) =>
            throw new InvalidOperationException("Windows Media Controls refused the read (test double).");

        public Task<bool> TryPauseAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> TryPlayAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();

        // Kept only so the field-like event above counts as used; nothing under test raises or reads it.
        internal void RaisePlaybackInfoChanged(string sessionId) => PlaybackInfoChanged?.Invoke(this, sessionId);
    }

    private TempFolder _temp = null!;
    private CapturingLog _log = null!;
    private JsonSettingsStore _settings = null!;
    private FakeWidgetStatus _status = null!;
    private FakeDeviceMonitor _deviceMonitor = null!;
    // AutoPauseService reads TimeProvider.System for "now" (mirroring the production wiring exactly, since
    // AutoPauseService itself is given TimeProvider.System, not a fake), so the reading's own timestamp must
    // be genuinely current too, or AutoPause.ApplyAsync's own staleness check (nowUtc - readingAtUtc) would
    // hold every pause off regardless of what this test is trying to prove.
    private static DateTimeOffset At => DateTimeOffset.UtcNow;

    [TestInitialize]
    public void Setup()
    {
        _temp = new TempFolder();
        _log = new CapturingLog();
        _settings = new JsonSettingsStore(_temp.File("settings.json"), _log);
        _status = new FakeWidgetStatus();
        _deviceMonitor = new FakeDeviceMonitor();
    }

    public void Dispose() => _temp.Dispose();

    // The exact recipe WidgetStatusService.RecomputeThisPcLocked itself uses to call
    // CoordinatorRules.WatchedContainer: no boot block status (null, so the pinned/settings path is not
    // shadowed by it), default settings (PinnedContainerId is Guid.Empty, so it is skipped too), and a
    // device snapshot whose Target container is the one value the method can then return. Setting
    // DefaultRenderContainerId to that same container is what makes "renders to the AirPods" true inside
    // AutoPause.ApplyAsync (defaultRenderContainerId == watchedContainerId).
    private static DeviceSnapshot RendersToAirPods()
    {
        DeviceSnapshot snapshot = Phase1.Phase1Fixtures.Target(ConnectionState.Connected);
        return snapshot with { DefaultRenderContainerId = Phase1.Phase1Fixtures.AirPodsContainer };
    }

    private AutoPauseService NewService(AutoPause autoPause) =>
        new(_status, _deviceMonitor, () => null, _settings, autoPause, TimeProvider.System, _log);

    private static DecodedReading Reading(bool? left, bool? right) =>
        new(new PartReading(null, null, left), new PartReading(null, null, right), PartReading.Unknown, null, null);

    [TestMethod]
    public void ABudLeavingTheEarWhileThisPcRendersToTheAirPodsPausesTheOnlyPlayingSession()
    {
        var sessions = new FakeMediaSessions
        {
            Sessions = { new MediaSessionView("app.exe", MediaPlaybackState.Playing, true, true, "app.exe") },
        };
        var autoPause = new AutoPause(sessions, () => true, () => true, _log);
        using AutoPauseService service = NewService(autoPause);

        _deviceMonitor.Current = RendersToAirPods();
        _status.Current = WidgetSnapshot.Empty(WidgetWatcherState.Started, claimExists: true) with { Where = AirPodsWhere.ThisPc };

        _status.Raise(Reading(left: true, right: null), At);
        _status.Raise(Reading(left: false, right: null), At);

        string[] expected = { "app.exe" };
        CollectionAssert.AreEqual(expected, sessions.PauseCalls);
    }

    // AutoPauseService always passes Owned, the only verdict that raises OwnedReadingApplied; the only
    // way to observe, from outside AutoPause, which literal AutoPauseService actually passes is behavioural:
    // if a stranger verdict (NoClaim, ModelOrColourMismatch, ...) reached ApplyAsync instead, the pause below
    // would never happen (AutoPauseTests.DoesNotPauseOnAStrangerReading proves that from AutoPause's own
    // side). This is the same scenario as the test above, kept separate and named for what it is asserting:
    // AutoPauseService's hard-coded stand-in is exercised, not a stranger verdict slipping through.
    [TestMethod]
    public void PassesTheOwnedVerdictLiteralSoTheStrangerVerdictPathIsNeverTaken()
    {
        var sessions = new FakeMediaSessions
        {
            Sessions = { new MediaSessionView("app.exe", MediaPlaybackState.Playing, true, true, "app.exe") },
        };
        var autoPause = new AutoPause(sessions, () => true, () => true, _log);
        using AutoPauseService service = NewService(autoPause);

        _deviceMonitor.Current = RendersToAirPods();
        _status.Current = WidgetSnapshot.Empty(WidgetWatcherState.Started, claimExists: true) with { Where = AirPodsWhere.ThisPc };

        _status.Raise(Reading(left: true, right: null), At);
        _status.Raise(Reading(left: false, right: null), At);

        Assert.AreEqual(1, sessions.PauseCalls.Count, "A stranger verdict would never reach TryPauseAsync at all (AutoPauseTests.DoesNotPauseOnAStrangerReading).");
    }

    [TestMethod]
    public void NeverCallsAutoPauseWhenNoOwnedReadingIsEverRaised()
    {
        var sessions = new FakeMediaSessions
        {
            Sessions = { new MediaSessionView("app.exe", MediaPlaybackState.Playing, true, true, "app.exe") },
        };
        var autoPause = new AutoPause(sessions, () => true, () => true, _log);
        using AutoPauseService service = NewService(autoPause);
        _deviceMonitor.Current = RendersToAirPods();

        Assert.AreEqual(0, sessions.PauseCalls.Count, "Nothing was ever raised: OwnedReadingApplied's own only-for-owned-readings contract is WidgetStatusServiceTests' to pin, not this class'.");
    }

    [TestMethod]
    public void AnExceptionFromApplyAsyncIsCaughtAndLoggedNotThrown()
    {
        var autoPause = new AutoPause(new ThrowingMediaSessions(), () => true, () => true, _log);
        using AutoPauseService service = NewService(autoPause);

        _deviceMonitor.Current = RendersToAirPods();
        _status.Current = WidgetSnapshot.Empty(WidgetWatcherState.Started, claimExists: true) with { Where = AirPodsWhere.ThisPc };

        _status.Raise(Reading(left: true, right: null), At);
        _status.Raise(Reading(left: false, right: null), At); // would otherwise trigger the read that throws

        Assert.IsTrue(_log.Has(LogLevel.Error, "ApplyAsync threw"), "The media-session exception must be logged, not silently swallowed.");
    }

    [TestMethod]
    public void DisposeStopsFeedingAutoPause()
    {
        var sessions = new FakeMediaSessions
        {
            Sessions = { new MediaSessionView("app.exe", MediaPlaybackState.Playing, true, true, "app.exe") },
        };
        var autoPause = new AutoPause(sessions, () => true, () => true, _log);
        var service = NewService(autoPause);
        _deviceMonitor.Current = RendersToAirPods();
        _status.Current = WidgetSnapshot.Empty(WidgetWatcherState.Started, claimExists: true) with { Where = AirPodsWhere.ThisPc };
        _status.Raise(Reading(left: true, right: null), At);

        service.Dispose();
        _status.Raise(Reading(left: false, right: null), At);

        Assert.AreEqual(0, sessions.PauseCalls.Count, "A disposed service must not react to a reading raised after Dispose.");
    }
}
