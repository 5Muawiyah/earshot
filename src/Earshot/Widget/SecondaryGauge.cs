using Earshot.Contracts;

namespace Earshot.Widget;

// What a secondary gauge is built from: everything it cannot make itself, so a test can give it fakes.
internal sealed record SecondaryGaugeParts(
    Func<ITaskbarReader> ReaderFactory,
    Func<IGaugeSurface> SurfaceFactory,
    Func<GaugeControllerSettings> Settings,
    ILog Log,
    TimeProvider Time,
    Func<IGaugeCoverProbe?> CoverProbe,
    Action<Action> UiPost,
    Func<ForegroundWindowReading?> Foreground,
    int PollIntervalMs,
    TrayIconVotes Votes);

// The log, with the display a gauge is on named in every line it writes, so two gauges' lines can be told apart. A line that
// starts "Gauge " keeps that start ("Gauge [Display 2] shown at ..."), so a search for the gauge's lines still finds them all.
internal sealed class DisplayTaggedLog(ILog inner, string display) : ILog
{
    public void Write(LogLevel level, string message, Exception? ex = null)
    {
        string tagged = message.StartsWith(GaugeEventLog.Prefix, StringComparison.Ordinal)
            ? GaugeEventLog.Prefix + "[" + display + "] " + message[GaugeEventLog.Prefix.Length..]
            : "[" + display + "] " + message;
        inner.Write(level, tagged, ex);
    }
}

// The gauge on one other display's taskbar, while Gauge display is All displays. It is the whole of what the main gauge is, for its
// own display: its own controller (placement, raising, hiding for a full-screen window on that display), its own window, its own
// taskbar watcher reading that display's taskbar, and its own scale. Nothing is shared with the main gauge but the tray icon, which
// stays out of the way while any gauge is shown (TrayIconVotes).
//
// Threads: the watcher reads on its own worker thread and posts every result to the UI thread, which is where the controller, its
// timers and the window are touched. Dispose stops the watcher first, so no read starts after it, and a result already posted when
// it runs is dropped (Disposed), so a removed gauge is never drawn on or moved after its window is gone.
internal sealed class SecondaryGauge : IDisposable
{
    private readonly GaugeController _controller;
    private readonly TaskbarWatcher _watcher;
    private readonly TrayIconVotes.Voter _vote;
    private readonly Action<SecondaryGauge> _laidOut;
    private IGaugeSurface? _surface;
    private volatile ShownGaugeForWorker? _shownForWorker;
    private int _dpi = 96;

    public SecondaryGauge(DisplayInfo display, string name, SecondaryGaugeParts parts, Action<SecondaryGauge> laidOut)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(laidOut);
        DisplayId = display.Id;
        Name = name;
        _laidOut = laidOut;
        _vote = parts.Votes.NewVoter();
        ILog log = new DisplayTaggedLog(parts.Log, name);
        _controller = new GaugeController(
            () =>
            {
                IGaugeSurface created = parts.SurfaceFactory();
                _surface = created;
                return created;
            },
            _vote, parts.Settings, log, parts.Time, parts.CoverProbe(), parts.UiPost, parts.Foreground);
        _controller.CardRequested += (_, _) => CardRequested?.Invoke(this, EventArgs.Empty);
        _controller.ToggleRequested += (_, _) => ToggleRequested?.Invoke(this, EventArgs.Empty);
        _controller.MenuRequested += (_, point) => MenuRequested?.Invoke(this, point);
        string id = display.Id;
        _watcher = new TaskbarWatcher(
            parts.ReaderFactory(), () => _shownForWorker?.Value, OnResult, parts.UiPost, log, parts.Time, parts.PollIntervalMs, () => id);
        _watcher.Start();
        _watcher.Poke();
    }

    // The display this gauge is for (DisplayInfo.Id) and its plain name.
    public string DisplayId { get; }

    public string Name { get; }

    public bool Disposed { get; private set; }

    // This display's scale, from its own last read: what a card opened from this gauge is drawn at.
    public int Dpi => _dpi;

    public GaugeState State => _controller.State;

    // The gauge's rectangle while it is on screen, or null: where a card for this gauge goes above.
    public Rectangle? ShownBounds => !Disposed && _controller.State is GaugeState.Shown shown ? shown.Bounds : null;

    public event EventHandler? CardRequested;

    public event EventHandler? ToggleRequested;

    public event EventHandler<Point>? MenuRequested;

    // A read of this display's taskbar, on the UI thread. A read that fell back to another display's taskbar (this display
    // is gone, or shows none) is no taskbar for this gauge: it must not be drawn on the main display's bar a second time. Nor is
    // a read of the main taskbar that says nothing of falling back, which is what this display's reader gives once Windows has made
    // this display the main one.
    private void OnResult(ITaskbarReader.Result result)
    {
        if (Disposed)
        {
            return;
        }

        ITaskbarReader.Result applied = result;
        if (result.Layout is { } layout)
        {
            if (layout.DisplayFallback != DisplayFallbackReason.None || !layout.IsSecondary)
            {
                applied = ITaskbarReader.Result.Fail(new TaskbarReadFailure(
                    TaskbarReadFailureStep.NoTaskbar, StepOutcomes.FromWin32("find-window:Shell_SecondaryTrayWnd", 0, ok: false)));
            }
            else
            {
                _dpi = layout.Dpi;
            }
        }

        _controller.OnLayout(applied);
        _shownForWorker = _controller.State is GaugeState.Shown shown && _surface is { IsDisposed: false, WindowHandle: not 0 } surface
            ? new ShownGaugeForWorker(new ShownGauge(shown.Bounds, surface.WindowHandle))
            : null;
        _laidOut(this);
    }

    // Draws the gauge for this snapshot where it is, at this display's own scale. Does nothing unless it is shown.
    public void Render(WidgetSnapshot snapshot, DateTimeOffset now, GaugeDisplaySettings settings, Color ink, string fontFamily)
    {
        if (!Disposed && _controller.State is GaugeState.Shown shown && _surface is { IsDisposed: false } surface)
        {
            surface.Render(snapshot, now, settings, _dpi, shown.Bounds, ink, fontFamily);
        }
    }

    public void OnForegroundChanged(string foregroundClass) => _controller.OnForegroundChanged(foregroundClass);

    public void OnShellWindowChanged(string windowClass, bool shown) => _controller.OnShellWindowChanged(windowClass, shown);

    public void NotifyFullScreenApp(bool opening) => _controller.NotifyFullScreenApp(opening);

    public void Poke() => _watcher.Poke();

    public void ResetBackoff() => _watcher.ResetBackoff();

    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;
        _watcher.Dispose();
        _shownForWorker = null;
        _controller.Dispose();
        _vote.Dispose();
    }

    // Boxed so the worker thread's read is one reference load, as the main gauge's is (ShownGauge is a struct).
    private sealed class ShownGaugeForWorker(ShownGauge value)
    {
        public ShownGauge Value { get; } = value;
    }
}
