using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Widget;

// Why the gauge is not shown. An auto-hidden bar that has slid away is expressed by GaugePlacement
// returning no rectangle (NoFreeSpace) once the on-screen part of the bar is thinner than its own
// thickness: this build gives it no fast path of its own ahead of a read, it simply falls out of the next
// poll. A full-screen application opening does get a fast path, NotifyFullScreenApp, called from the appbar
// notification callback (ABN_FULLSCREENAPP) rather than waiting for the next poll to see it; it has its own
// reason (FullScreenNotified) so the log tells it from the polled equivalent (QUNS_RUNNING_D3D_FULL_SCREEN,
// QUNS_PRESENTATION_MODE), which still exists for a state the appbar notification does not cover. Both end
// up Hidden with the icon shown: every transition into Hidden calls IGaugeSurface.HideWindow on the
// existing surface, so the window itself disappears rather than merely leaving the tray icon to say so; a
// transition into Off goes further and disposes the surface outright.
internal enum HiddenReason { NoTaskbar, ReadFailed, NoFreeSpace, Covered, NotificationState, FullScreenNotified, WindowFailed }

// The controller's state.
internal abstract record GaugeState
{
    private GaugeState()
    {
    }

    public sealed record Off : GaugeState;

    public sealed record Hidden(HiddenReason Reason) : GaugeState;

    public sealed record Shown(Rectangle Bounds) : GaugeState;
}

// What the controller needs from the gauge window. GaugeWindow is the real implementation; tests supply
// a fake (FakeGaugeSurface).
internal interface IGaugeSurface : IDisposable
{
    bool IsDisposed { get; }

    // The window handle, or 0 when there is none yet.
    nint WindowHandle { get; }

    event EventHandler? LeftClicked;

    event EventHandler<Point>? RightClicked;

    // SetWindowPos(HWND_TOPMOST, SWP_NOACTIVATE | SWP_SHOWWINDOW) then UpdateLayeredWindow.
    StepOutcome ShowAt(Rectangle bounds);

    // SetWindowPos(SWP_NOZORDER | SWP_NOACTIVATE).
    StepOutcome MoveTo(Rectangle bounds);

    // SetWindowPos(HWND_TOPMOST, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE): re-raised above whatever
    // opened after it.
    StepOutcome Raise();

    // The window this one is owned by (GetWindow with GW_OWNER), or 0 when it has none.
    nint OwnerWindow { get; }

    // Makes the window the owned window of owner (SetWindowLongPtr with GWLP_HWNDPARENT), so the system keeps it above
    // that window wherever that window is raised. The window is not a child and is not moved.
    StepOutcome SetOwner(nint owner);

    // Draws the gauge for this snapshot at these bounds.
    void Render(WidgetSnapshot snapshot, DateTimeOffset now, GaugeDisplaySettings settings, int dpi, Rectangle bounds, Color ink, string fontFamily);

    void HideWindow();
}

// What the controller needs from the tray icon. TrayContext's own NotifyIcon satisfies this directly.
internal interface ITrayIconVisibility
{
    bool Visible { set; }
}

// The settings the controller reads each layout. A snapshot, not a live settings object, so a test can
// hand it a fixed value without building a whole ISettingsStore.
internal readonly record struct GaugeControllerSettings(bool Enabled, bool LeftClickConnects, GaugePosition Position = GaugePosition.RightEnd);

// The gauge's state machine: applies each TaskbarWatcher result, shows, moves or hides the window,
// toggles the tray icon, and routes clicks. UI thread only.
//
// Debounce: the icon hides 2 s after the gauge has been continuously shown, and reappears at once
// whenever the gauge leaves Shown for any reason. Checked on every OnLayout call rather than a separate
// timer, so tests drive it with ManualTime instead of a real clock.
//
// A failed read keeps the gauge where it was. Only "no Shell_TrayWnd" is a definite sign that there is no
// bar to sit on; any other failure (a UI Automation read that timed out while the shell was busy, a thrown
// read) counts, and the gauge hides after ReadFailureTolerance failures in a row.
//
// Being put back on top: the gauge is the taskbar's owned window (EnsureOwner), and an owned window is always above its
// owner in the z-order, so wherever the shell raises the taskbar the system raises the gauge with it, in the same step
// and with no frame between. What follows is the safety net for a gauge that could not be owned, or that another
// program's topmost window covers.
//
// The shell raises the taskbar above the topmost band when Start, a flyout or a
// full-screen application closes, and again after a click on it, which leaves an unowned gauge under it. A foreground
// change (OnForegroundChanged) or a window Explorer showed or hid (OnShellWindowChanged) looks at what is over
// the gauge's centre now and once more 250 ms later, and raises the gauge only when that window is the taskbar.
// Whenever any cover is found, by those or by the poll, the same look repeats every FastCheckInterval for
// FastCheckDuration after the last cover, since the taskbar covers the gauge again a moment after a raise with no
// event to say so; a cover without an event then lasts one interval, not one poll. The poll's own check stays as
// the safety net. Earshot's own windows over the gauge (its tooltip, the card) are never a cover.
//
// A raise that does not hold is not repeated at once. When the same cover is found again right after a raise, the next raise
// waits longer each time (RaiseGap), up to RaiseGapLimit; the gauge being found on top a second after a raise ends that,
// so the next cover is answered at once again. Each raise shows the gauge for a moment and it is covered again, so a
// raise every quarter second for as long as the cover lasts is a flash every quarter second.
internal sealed class GaugeController : IDisposable
{
    public static readonly TimeSpan IconHideDebounce = TimeSpan.FromSeconds(2);

    // Three is one poll interval more than the longest read seen while a full-screen state changed (606 ms
    // in the field log), and few enough that a real loss is noticed in about three seconds.
    public const int ReadFailureTolerance = 3;

    // The gap between the two looks at what is over the gauge after an event.
    public static readonly TimeSpan SecondLookDelay = TimeSpan.FromMilliseconds(250);

    // How often the gauge is looked at after a cover was found, and for how long after the last one. A quarter of a
    // second is the second look's own gap, short enough that a covered gauge is back on top before it is noticed
    // and cheap enough (one point query, no UI Automation) to run for ten seconds.
    public static readonly TimeSpan FastCheckInterval = TimeSpan.FromMilliseconds(250);

    public static readonly TimeSpan FastCheckDuration = TimeSpan.FromSeconds(10);

    // At most this many raises in any RaiseWindow. Four in a second is one per FastCheckInterval, the most a check
    // that runs that often can ask for, so a taskbar that covers the gauge again after every raise is answered every
    // time; a real loop (two windows raising each other) is held to that rate and never runs away. It is a window
    // that slides, not a stop until something confirms: a gauge left covered is the defect this replaced.
    public const int RaisesPerWindow = 4;

    public static readonly TimeSpan RaiseWindow = TimeSpan.FromSeconds(1);

    // A raise held when the gauge is found on top this long after it. Until then a cover found again counts against it.
    public static readonly TimeSpan RaiseHoldConfirm = TimeSpan.FromSeconds(1);

    // The longest wait between two raises that did not hold.
    public static readonly TimeSpan RaiseGapLimit = TimeSpan.FromSeconds(8);

    // How long the next raise waits after this many raises in a row that did not hold: none after none, a quarter second
    // after one (the bar that rose a moment late is still answered by the second look), then doubling to RaiseGapLimit.
    public static TimeSpan RaiseGap(int unheldRaises) => unheldRaises <= 0
        ? TimeSpan.Zero
        : TimeSpan.FromTicks(Math.Min(RaiseGapLimit.Ticks, SecondLookDelay.Ticks << Math.Min(unheldRaises - 1, 8)));

    // The limit is reported at most this often, so a long fight is one line a minute, not one a second.
    private static readonly TimeSpan LimitWarnInterval = TimeSpan.FromMinutes(1);

    private const string ShellTrayWndClass = "Shell_TrayWnd";

    // The taskbar on every other display is a window of this class, so a gauge on one is covered by it, not by the main taskbar's.
    private const string ShellSecondaryTrayWndClass = "Shell_SecondaryTrayWnd";

    private readonly Func<IGaugeSurface> _createSurface;
    private readonly ITrayIconVisibility _trayIcon;
    private readonly Func<GaugeControllerSettings> _settings;
    private readonly ILog _log;
    private readonly TimeProvider _time;
    private readonly IGaugeCoverProbe? _coverProbe;
    private readonly Action<Action> _uiPost;
    private readonly Func<ForegroundWindowReading?>? _foregroundProbe;

    // The display the last layout was read for, and how many displays there were: what a full-screen signal is
    // judged against. One display until a layout says otherwise, so a signal that comes first hides as it always did.
    private Rectangle _displayBounds;
    private int _displayCount = 1;
    private bool _fullScreenPending;
    private string? _lastIgnoredKey;
    private DisplayFallbackReason _lastDisplayFallback;
    private int? _lastDisplayProblemCode;

    private IGaugeSurface? _surface;
    private GaugeState _state = new GaugeState.Off();
    private long _shownSinceTimestamp;
    private bool _iconHiddenForShown;
    private HiddenReason? _lastLoggedReason;
    private int? _lastLoggedCode;
    private string? _lastHideKey;
    private int _consecutiveReadFailures;
    private readonly Queue<long> _raiseStamps = new();
    private int _unheldRaises;
    private long _lastRaiseTimestamp;
    private nint _ownerFailedFor;
    private bool _ownerHeldOff;
    private long _lastLimitWarnTimestamp;
    private bool _limitWarnedBefore;
    private ITimer? _secondLook;
    private CoverTrigger _secondLookTrigger;
    private ITimer? _fastChecks;
    private long _lastCoverTimestamp;
    private string? _lastLeftUnderClass;
    private bool _disposed;

    // What made the gauge look at its cover: the log line for a raise says which.
    private enum CoverTriggerKind { ForegroundChange, ShellWindowShown, ShellWindowHidden, Recheck }

    private readonly record struct CoverTrigger(CoverTriggerKind Kind, string Class);

    // coverProbe: what is at the gauge's centre; null means the foreground-change path has nothing to look
    // with and does nothing (the poll still works). uiPost: how a timer callback, which runs on a pool
    // thread, gets back to the UI thread; null runs it where it is called from (a test's manual clock).
    public GaugeController(
        Func<IGaugeSurface> createSurface, ITrayIconVisibility trayIcon, Func<GaugeControllerSettings> settings, ILog log, TimeProvider time,
        IGaugeCoverProbe? coverProbe = null, Action<Action>? uiPost = null, Func<ForegroundWindowReading?>? foregroundProbe = null)
    {
        ArgumentNullException.ThrowIfNull(createSurface);
        ArgumentNullException.ThrowIfNull(trayIcon);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(time);
        _createSurface = createSurface;
        _trayIcon = trayIcon;
        _settings = settings;
        _log = log;
        _time = time;
        _coverProbe = coverProbe;
        _uiPost = uiPost ?? (static action => action());
        _foregroundProbe = foregroundProbe;
    }

    public GaugeState State => _state;

    // Why the last read was not on the display the owner chose, or None.
    public DisplayFallbackReason DisplayFallback => _lastDisplayFallback;

    // Raised on a left click when LeftClickConnects is off (the default): the owner asked to see the card.
    public event EventHandler? CardRequested;

    // Raised on a left click when LeftClickConnects is on: the owner asked to connect or disconnect
    // straight away, the v1 behaviour.
    public event EventHandler? ToggleRequested;

    // Raised on a right click, at the screen point clicked: the same context menu the tray icon shows.
    public event EventHandler<Point>? MenuRequested;

    // Applies one TaskbarWatcher result. Called on the UI thread, once per read.
    public void OnLayout(ITaskbarReader.Result result)
    {
        if (_disposed)
        {
            return;
        }

        GaugeControllerSettings settings = _settings();
        if (!settings.Enabled)
        {
            TransitionOff();
            return;
        }

        if (result.Failure is { } failure)
        {
            OnFailedRead(failure);
            return;
        }

        _consecutiveReadFailures = 0;
        TaskbarLayout layout = result.Layout!;
        _displayBounds = layout.MonitorBounds;
        _displayCount = Math.Max(1, layout.DisplayCount);
        NoteDisplay(layout);
        if (layout.NotificationState is Shell.QUNS_BUSY or Shell.QUNS_RUNNING_D3D_FULL_SCREEN or Shell.QUNS_PRESENTATION_MODE)
        {
            // The state is global: it says a full-screen application exists somewhere. It hides this gauge only when
            // that application covers this gauge's display. Presentation settings are the owner's switch, not a
            // window, so they hide the gauge on every display as they always did.
            string quns = QunsName(layout.NotificationState);
            if (layout.NotificationState == Shell.QUNS_PRESENTATION_MODE ||
                FullScreenRule.CoversGaugeDisplay(_displayCount, layout.ForegroundWindow, _displayBounds))
            {
                string? where = _displayCount > 1 ? layout.ForegroundWindow?.DisplayLabel : null;
                TransitionHidden(HiddenReason.NotificationState, "NotificationState " + quns,
                    GaugeEventLog.HiddenNotificationState(quns, layout.NotificationState == Shell.QUNS_PRESENTATION_MODE ? null : where), outcome: null);
                return;
            }

            NoteFullScreenElsewhere(quns, layout.ForegroundWindow, layout.DisplayLabel);
        }
        else
        {
            _fullScreenPending = false;
            _lastIgnoredKey = null;
        }

        if (layout.Covered)
        {
            TransitionHidden(HiddenReason.Covered, "Covered " + GaugeEventLog.Describe(layout.CoveringWindow),
                GaugeEventLog.HiddenCovered(layout.CoveringWindow), outcome: null);
            return;
        }

        GaugeLayout gauge = GaugeLayout.For(layout.Dpi);
        PlacementResult placed = GaugePlacement.Place(layout, gauge, settings.Position);
        if (placed.Bounds is not { } bounds)
        {
            if (!(_state is GaugeState.Hidden { Reason: HiddenReason.NoFreeSpace } && _lastHideKey == "NoFreeSpace " + placed.Failure))
            {
                _log.Write(LogLevel.Debug, GaugeEventLog.PlacementFailed(placed.Failure));
            }

            TransitionHidden(HiddenReason.NoFreeSpace, "NoFreeSpace " + placed.Failure, GaugeEventLog.HiddenNoFreeSpace(), outcome: null);
            return;
        }

        ShowOrMove(bounds, layout);
        CheckIconDebounce();
    }

    // A read failed. No Shell_TrayWnd is definite: there is no bar to sit on. Anything else is counted, and
    // while the gauge is on screen it stays where it is until ReadFailureTolerance failures in a row.
    private void OnFailedRead(TaskbarReadFailure failure)
    {
        if (failure.Step == TaskbarReadFailureStep.NoTaskbar)
        {
            TransitionHidden(HiddenReason.NoTaskbar, "NoTaskbar", GaugeEventLog.HiddenNoTaskbar(), failure.Outcome);
            return;
        }

        _consecutiveReadFailures++;
        if (_state is GaugeState.Shown shown && _consecutiveReadFailures < ReadFailureTolerance)
        {
            _log.Write(LogLevel.Debug, GaugeEventLog.KeptAfterFailedRead(shown.Bounds, failure.Step.ToString(), failure.Outcome, _consecutiveReadFailures, ReadFailureTolerance));
            return;
        }

        TransitionHidden(
            HiddenReason.ReadFailed, "ReadFailed " + failure.Step + " " + failure.Outcome.Code,
            GaugeEventLog.HiddenReadFailed(failure.Step.ToString(), failure.Outcome, _consecutiveReadFailures), failure.Outcome);
    }

    // ABN_FULLSCREENAPP from the appbar notification callback, decoded by the caller into opening/closing.
    // Opening hides the gauge at once, without waiting for TaskbarWatcher's next scheduled read, with its own
    // reason: FullScreenNotified, not the NotificationState a polled QUNS_BUSY/QUNS_RUNNING_D3D_FULL_SCREEN/
    // QUNS_PRESENTATION_MODE result gives. Closing does nothing here: the taskbar's actual state still
    // needs re-reading before anything is shown again, so the caller pokes TaskbarWatcher for a fresh read
    // instead of this method forcing a show. Does nothing while the widget is off (Off is not a state this
    // notification should move out of): the caller poking a disposed or never-built watcher is already
    // harmless on its own.
    //
    // The notice is global (it carries no window and no monitor, and the appbar message goes to every appbar), so it
    // is a trigger to look, not the verdict: the gauge hides only when the foreground window covers the gauge's
    // display. The foreground may not have moved to the full-screen window yet, so an opening notice stays pending
    // until a poll says no full-screen state remains or a closing notice arrives, and each foreground change while it
    // is pending looks again.
    public void NotifyFullScreenApp(bool opening)
    {
        if (_disposed || _state is GaugeState.Off)
        {
            return;
        }

        if (!opening)
        {
            _fullScreenPending = false;
            return;
        }

        _fullScreenPending = true;
        EvaluateFullScreenNotice();
    }

    private void EvaluateFullScreenNotice()
    {
        ForegroundWindowReading? foreground = _displayCount > 1 ? _foregroundProbe?.Invoke() : null;
        if (FullScreenRule.CoversGaugeDisplay(_displayCount, foreground, _displayBounds))
        {
            string? where = _displayCount > 1 ? foreground?.DisplayLabel : null;
            TransitionHidden(HiddenReason.FullScreenNotified, "FullScreenNotified", GaugeEventLog.HiddenFullScreenNotified(where), outcome: null);
            return;
        }

        NoteFullScreenElsewhere("FullScreenNotified", foreground, "");
    }

    private void NoteFullScreenElsewhere(string signal, ForegroundWindowReading? window, string gaugeDisplay)
    {
        string key = signal + "|" + window?.DisplayLabel + "|" + window?.Identity?.ClassName;
        if (string.Equals(_lastIgnoredKey, key, StringComparison.Ordinal))
        {
            return;
        }

        _lastIgnoredKey = key;
        _log.Write(LogLevel.Debug, GaugeEventLog.FullScreenOnOtherDisplay(signal, window?.Identity, window?.DisplayLabel ?? "?", gaugeDisplay));
    }

    // What the read says about the display the gauge is on: a failure reading the displays once per code, and a
    // change between the chosen display and the main display's taskbar as one line each way.
    private void NoteDisplay(TaskbarLayout layout)
    {
        if (layout.DisplayProblem is { } problem)
        {
            if (_lastDisplayProblemCode != problem.Code)
            {
                _lastDisplayProblemCode = problem.Code;
                _log.Warn(GaugeEventLog.DisplayProblem(problem));
            }
        }
        else
        {
            _lastDisplayProblemCode = null;
        }

        if (layout.DisplayFallback == _lastDisplayFallback)
        {
            return;
        }

        DisplayFallbackReason before = _lastDisplayFallback;
        _lastDisplayFallback = layout.DisplayFallback;
        switch (layout.DisplayFallback)
        {
            case DisplayFallbackReason.NotConnected:
                _log.Info(GaugeEventLog.DisplayNotConnected());
                break;
            case DisplayFallbackReason.TaskbarNotShown:
                _log.Info(GaugeEventLog.DisplayTaskbarNotShown());
                break;
            default:
                if (before != DisplayFallbackReason.None)
                {
                    _log.Info(GaugeEventLog.DisplayBack(layout.DisplayLabel));
                }

                break;
        }
    }

    // The foreground window changed to a window of the given class (a class only, never a title). Looks at
    // what is over the gauge now and again after SecondLookDelay, since the shell can raise the taskbar a
    // moment after the change. Does nothing unless the gauge is on screen. UI thread.
    public void OnForegroundChanged(string foregroundClass)
    {
        if (!_disposed && _fullScreenPending && _state is GaugeState.Shown)
        {
            EvaluateFullScreenNotice();
        }

        OnCoverEvent(new CoverTrigger(CoverTriggerKind.ForegroundChange, foregroundClass));
    }

    // Explorer showed (or hid) a top-level window of the given class (a class only, never a title): a flyout
    // opening or closing is what makes the shell raise the taskbar, with no change of the foreground window to
    // say so. Treated as a foreground change is. UI thread.
    public void OnShellWindowChanged(string windowClass, bool shown) =>
        OnCoverEvent(new CoverTrigger(shown ? CoverTriggerKind.ShellWindowShown : CoverTriggerKind.ShellWindowHidden, windowClass));

    private void OnCoverEvent(CoverTrigger trigger)
    {
        if (_disposed || _state is not GaugeState.Shown)
        {
            return;
        }

        CheckCover(trigger);
        if (_secondLook is not null)
        {
            // One second look is already pending: this change is covered by it.
            _secondLookTrigger = trigger;
            return;
        }

        _secondLookTrigger = trigger;
        _secondLook = _time.CreateTimer(_ => _uiPost(OnSecondLook), null, SecondLookDelay, Timeout.InfiniteTimeSpan);
    }

    // The owner turned the setting off, or Enabled was already false: watcher stopped, icon shown,
    // window disposed. Idempotent.
    public void TurnOff() => TransitionOff();

    // Takes the gauge off the taskbar's ownership and keeps it off until ResumeOwner. Called before this thread makes a wait
    // that holds it up (a shut-down or sleep hand-back, the closing of the tray): the gauge is then tied to no window of the
    // shell, whatever the state of its input queue. The safety net (raising when covered) still keeps the gauge on top
    // meanwhile. A gauge that was not owned only has the hold-off set.
    public void ReleaseOwner()
    {
        _ownerHeldOff = true;
        if (_surface is not { IsDisposed: false } surface || surface.OwnerWindow == 0)
        {
            return;
        }

        StepOutcome outcome = surface.SetOwner(0);
        if (outcome.Ok)
        {
            _log.Info(GaugeEventLog.OwnerReleased());
            return;
        }

        _log.Warn(GaugeEventLog.OwnerReleaseFailed(outcome));
    }

    // Lets the next layout own the gauge again (the machine woke).
    public void ResumeOwner() => _ownerHeldOff = false;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _secondLook?.Dispose();
        _secondLook = null;
        StopFastChecks();
        DisposeSurface();
    }

    private void OnSecondLook()
    {
        _secondLook?.Dispose();
        _secondLook = null;
        if (_disposed || _state is not GaugeState.Shown)
        {
            return;
        }

        CheckCover(_secondLookTrigger);
    }

    // A cover was found: look again every FastCheckInterval until FastCheckDuration has passed since the last one.
    private void NoteCover()
    {
        if (_coverProbe is null)
        {
            return;
        }

        _lastCoverTimestamp = _time.GetTimestamp();
        _fastChecks ??= _time.CreateTimer(_ => _uiPost(OnFastCheck), null, FastCheckInterval, FastCheckInterval);
    }

    private void OnFastCheck()
    {
        if (_fastChecks is null)
        {
            return;
        }

        if (_disposed || _state is not GaugeState.Shown || _time.GetElapsedTime(_lastCoverTimestamp) >= FastCheckDuration)
        {
            StopFastChecks();
            return;
        }

        CheckCover(new CoverTrigger(CoverTriggerKind.Recheck, ""));
    }

    private void StopFastChecks()
    {
        _fastChecks?.Dispose();
        _fastChecks = null;
    }

    // Asks what is at the gauge's centre. The gauge itself: nothing to do. The taskbar: put the gauge back
    // on top, within the limits. Anything else (a flyout, a full-screen window, another topmost window)
    // belongs over the gauge for now and is left alone.
    private void CheckCover(CoverTrigger trigger)
    {
        if (_coverProbe is null || _surface is not { IsDisposed: false } surface || _state is not GaugeState.Shown shown)
        {
            return;
        }

        GaugeCover cover = _coverProbe.Probe(new ShownGauge(shown.Bounds, surface.WindowHandle));
        if (cover.IsGauge || cover.BelongsToThisProcess)
        {
            _lastLeftUnderClass = null;
            NoteOnTop();
            return;
        }

        NoteCover();
        if (!string.Equals(cover.RootClassName, ShellTrayWndClass, StringComparison.Ordinal) &&
            !string.Equals(cover.RootClassName, ShellSecondaryTrayWndClass, StringComparison.Ordinal))
        {
            // Once per window class: a flyout that stays open is found by every recheck.
            if (!string.Equals(_lastLeftUnderClass, cover.RootClassName, StringComparison.Ordinal))
            {
                _lastLeftUnderClass = cover.RootClassName;
                _log.Write(
                    LogLevel.Debug,
                    trigger.Kind == CoverTriggerKind.ForegroundChange
                        ? GaugeEventLog.LeftUnder(cover.RootClassName)
                        : GaugeEventLog.LeftUnderOtherwise(cover.RootClassName));
            }

            return;
        }

        _lastLeftUnderClass = null;
        if (!RaiseAllowed())
        {
            return;
        }

        var over = new WindowIdentity(cover.RootClassName, cover.BelongsToExplorer);
        RaiseSurface(surface, trigger.Kind switch
        {
            CoverTriggerKind.ForegroundChange => GaugeEventLog.RaisedAfterForegroundChange(over, trigger.Class),
            CoverTriggerKind.ShellWindowShown => GaugeEventLog.RaisedAfterShellWindow(over, trigger.Class, shown: true),
            CoverTriggerKind.ShellWindowHidden => GaugeEventLog.RaisedAfterShellWindow(over, trigger.Class, shown: false),
            _ => GaugeEventLog.RaisedOnRecheck(over),
        }, counted: true);
    }

    // The gauge was found on top. A second or more after a raise that is the raise holding, and the next cover is a new one.
    private void NoteOnTop()
    {
        if (_unheldRaises > 0 && _time.GetElapsedTime(_lastRaiseTimestamp) >= RaiseHoldConfirm)
        {
            _unheldRaises = 0;
        }
    }

    // Whether another raise may be made now: not before RaiseGap has passed since the last one when raises have not been
    // holding, and, for the event-driven checks (sliding), not more than RaisesPerWindow in any RaiseWindow.
    private bool RaiseAllowed(bool sliding = true)
    {
        long now = _time.GetTimestamp();
        if (_unheldRaises > 0 && _time.GetElapsedTime(_lastRaiseTimestamp, now) < RaiseGap(_unheldRaises))
        {
            return false;
        }

        if (!sliding)
        {
            return true;
        }

        while (_raiseStamps.Count > 0 && _time.GetElapsedTime(_raiseStamps.Peek(), now) >= RaiseWindow)
        {
            _ = _raiseStamps.Dequeue();
        }

        if (_raiseStamps.Count < RaisesPerWindow)
        {
            return true;
        }

        if (!_limitWarnedBefore || _time.GetElapsedTime(_lastLimitWarnTimestamp, now) >= LimitWarnInterval)
        {
            _limitWarnedBefore = true;
            _lastLimitWarnTimestamp = now;
            _log.Write(LogLevel.Warn, GaugeEventLog.RaiseCapReached(RaisesPerWindow, (int)RaiseWindow.TotalSeconds));
        }

        return false;
    }

    // Puts the gauge back on top and writes the line saying why. A failing raise is a window failure, the
    // same as a failing show or move. counted: the raise takes one of the sliding limit's places.
    private void RaiseSurface(IGaugeSurface surface, string line, bool counted)
    {
        StepOutcome outcome = surface.Raise();
        _lastRaiseTimestamp = _time.GetTimestamp();
        _unheldRaises++;
        if (counted)
        {
            _raiseStamps.Enqueue(_lastRaiseTimestamp);
        }

        if (!outcome.Ok)
        {
            LogFailureOnce(HiddenReason.WindowFailed, outcome);
            DisposeSurface();
            TransitionHidden(HiddenReason.WindowFailed, "WindowFailed " + outcome.Code, GaugeEventLog.HiddenWindowFailed(outcome), outcome: null);
            return;
        }

        _log.Info(line);
        if (RaiseGap(_unheldRaises) >= RaiseHoldConfirm)
        {
            _log.Info(GaugeEventLog.RaiseBackedOff(RaiseGap(_unheldRaises)));
        }
    }

    // The gauge is made the owned window of the taskbar it sits on. An owned window is always above its owner in the
    // z-order, so the system keeps the gauge above the taskbar wherever the shell raises it: no raise, and no frame with
    // the taskbar over the gauge, which is what a flash was. Done when there is a taskbar window to name and the gauge
    // is not already owned by it (a new window, or an Explorer that started again with a new taskbar window), and not
    // asked again for a window it failed for, so a refusal is one line and not one per poll.
    // https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#owned-windows
    private void EnsureOwner(IGaugeSurface surface, TaskbarLayout layout)
    {
        nint taskbar = layout.TaskbarHandle;
        if (_ownerHeldOff || taskbar == 0 || taskbar == _ownerFailedFor || surface.OwnerWindow == taskbar)
        {
            return;
        }

        StepOutcome outcome = surface.SetOwner(taskbar);
        if (outcome.Ok)
        {
            _ownerFailedFor = 0;
            _log.Info(GaugeEventLog.Owned(layout.IsSecondary));
            return;
        }

        _ownerFailedFor = taskbar;
        _log.Warn(GaugeEventLog.OwnerFailed(outcome));
    }

    private void ShowOrMove(Rectangle bounds, TaskbarLayout layout)
    {
        IGaugeSurface surface = EnsureSurface();
        EnsureOwner(surface, layout);
        bool wasShown = _state is GaugeState.Shown;
        Rectangle? previous = wasShown ? ((GaugeState.Shown)_state).Bounds : null;

        if (!wasShown)
        {
            string reason = ShownReason();
            StepOutcome shownOutcome = surface.ShowAt(bounds);
            if (!shownOutcome.Ok)
            {
                FailWindow(shownOutcome);
                return;
            }

            _log.Info(GaugeEventLog.Shown(bounds, reason));
        }
        else if (previous != bounds)
        {
            StepOutcome movedOutcome = surface.MoveTo(bounds);
            if (!movedOutcome.Ok)
            {
                FailWindow(movedOutcome);
                return;
            }

            _log.Info(GaugeEventLog.Moved(bounds));
        }
        else if (layout.GaugeCentreIsGauge == false && layout.WindowAtGaugeCentre is not { BelongsToThisProcess: true })
        {
            // Nothing moved, but the read's own WindowFromPoint check at the gauge's own centre (the
            // reader's GaugeCentreIsGauge) found something else there instead of the gauge itself: the
            // shell (or some other topmost window) has been drawn over it since the read that last
            // confirmed it was on top. A MoveTo to the same rectangle is a SetWindowPos no-op that would
            // leave the gauge invisible under whatever now sits there; Raise puts it back on top without
            // moving or resizing it. False, not just "not true": a genuinely unreadable point (no gauge
            // was shown for this read to check) is null, never treated as covered. One of Earshot's own
            // windows there (its tooltip, the card) is not a cover and falls through to "nothing to do".
            // The poll cannot loop faster than its own interval, so its raise is not counted against the
            // limit the event-driven checks share.
            NoteCover();
            if (RaiseAllowed(sliding: false))
            {
                RaiseSurface(surface, GaugeEventLog.RaisedByPoll(layout.WindowAtGaugeCentre), counted: false);
            }

            if (_state is not GaugeState.Shown)
            {
                return;
            }
        }
        else
        {
            // Nothing changed, and the read confirms the gauge is still the topmost window at its own
            // centre (or there was no shown gauge yet for this read to check in the first place).
            if (layout.GaugeCentreIsGauge == true)
            {
                NoteOnTop();
            }

            return;
        }

        bool enteringShown = _state is not GaugeState.Shown;
        _state = new GaugeState.Shown(bounds);
        _lastHideKey = null;
        if (enteringShown)
        {
            _shownSinceTimestamp = _time.GetTimestamp();
            _iconHiddenForShown = false;
            _trayIcon.Visible = true;
        }
    }

    // A window call failed: written once as an Error with its raw code, the surface is thrown away (a lost
    // handle is a fresh window next time) and the gauge hides.
    private void FailWindow(StepOutcome outcome)
    {
        LogFailureOnce(HiddenReason.WindowFailed, outcome);
        DisposeSurface();
        TransitionHidden(HiddenReason.WindowFailed, "WindowFailed " + outcome.Code, GaugeEventLog.HiddenWindowFailed(outcome), outcome: null);
    }

    // What the gauge was doing just before it is shown again, as the reason the log gives.
    private string ShownReason() => _state switch
    {
        GaugeState.Hidden { Reason: HiddenReason.NoTaskbar } => GaugeEventLog.ShownTaskbarBack,
        GaugeState.Hidden { Reason: HiddenReason.ReadFailed } => GaugeEventLog.ShownReadRecovered,
        GaugeState.Hidden { Reason: HiddenReason.NotificationState or HiddenReason.FullScreenNotified } => GaugeEventLog.ShownFullScreenClosed,
        GaugeState.Hidden { Reason: HiddenReason.NoFreeSpace } => GaugeEventLog.ShownFreeSpaceBack,
        GaugeState.Hidden { Reason: HiddenReason.Covered } => GaugeEventLog.ShownUncovered,
        GaugeState.Hidden { Reason: HiddenReason.WindowFailed } => GaugeEventLog.ShownWindowRecreated,
        _ => GaugeEventLog.ShownFirstLayout,
    };

    private void CheckIconDebounce()
    {
        if (_state is GaugeState.Shown && !_iconHiddenForShown &&
            _time.GetElapsedTime(_shownSinceTimestamp) >= IconHideDebounce)
        {
            _trayIcon.Visible = false;
            _iconHiddenForShown = true;
        }
    }

    // key tells one cause from another of the same HiddenReason (a different failing step, a different
    // covering window) so a change of cause is a line and a poll that finds the same thing again is not.
    private void TransitionHidden(HiddenReason reason, string key, string line, StepOutcome? outcome)
    {
        if (outcome is { } o)
        {
            LogFailureOnce(reason, o);
        }

        bool changed = _state is not GaugeState.Hidden hidden || hidden.Reason != reason || !string.Equals(_lastHideKey, key, StringComparison.Ordinal);
        _state = new GaugeState.Hidden(reason);
        _lastHideKey = key;
        _iconHiddenForShown = false;
        _trayIcon.Visible = true;
        StopFastChecks();
        _unheldRaises = 0;
        _surface?.HideWindow();
        if (changed)
        {
            _log.Info(line);
        }
    }

    private void TransitionOff()
    {
        // Always sets the icon visible and disposes any surface, even when already Off: the constructor
        // starts in Off before any layout is ever applied, so an "already there" guard here would leave
        // the icon in whatever state the caller set it to before the first call.
        bool wasOff = _state is GaugeState.Off;
        _state = new GaugeState.Off();
        _lastHideKey = null;
        _consecutiveReadFailures = 0;
        _trayIcon.Visible = true;
        StopFastChecks();
        _unheldRaises = 0;
        DisposeSurface();
        if (!wasOff)
        {
            _log.Info(GaugeEventLog.HiddenSettingOff());
        }
    }

    private void LogFailureOnce(HiddenReason reason, StepOutcome outcome)
    {
        if (_lastLoggedReason == reason && _lastLoggedCode == outcome.Code)
        {
            _log.Write(LogLevel.Debug, "Gauge " + reason + ": " + outcome.CodeName + " (repeat).");
            return;
        }

        _lastLoggedReason = reason;
        _lastLoggedCode = outcome.Code;
        _log.Error("Gauge " + reason + ": " + outcome.CodeName + " (" + outcome.Code + ") " + outcome.Detail);
    }

    private static string QunsName(int quns) => quns switch
    {
        Shell.QUNS_BUSY => "QUNS_BUSY",
        Shell.QUNS_RUNNING_D3D_FULL_SCREEN => "QUNS_RUNNING_D3D_FULL_SCREEN",
        Shell.QUNS_PRESENTATION_MODE => "QUNS_PRESENTATION_MODE",
        _ => "QUNS_" + quns,
    };

    private IGaugeSurface EnsureSurface()
    {
        if (_surface is { IsDisposed: false } existing)
        {
            return existing;
        }

        if (_surface is not null)
        {
            _surface.LeftClicked -= OnLeftClicked;
            _surface.RightClicked -= OnRightClicked;
        }

        _ownerFailedFor = 0;
        IGaugeSurface created = _createSurface();
        created.LeftClicked += OnLeftClicked;
        created.RightClicked += OnRightClicked;
        _surface = created;
        return created;
    }

    private void DisposeSurface()
    {
        if (_surface is null)
        {
            return;
        }

        _surface.LeftClicked -= OnLeftClicked;
        _surface.RightClicked -= OnRightClicked;
        _surface.Dispose();
        _surface = null;
    }

    private void OnLeftClicked(object? sender, EventArgs e)
    {
        if (_settings().LeftClickConnects)
        {
            ToggleRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            CardRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnRightClicked(object? sender, Point point) => MenuRequested?.Invoke(this, point);
}
