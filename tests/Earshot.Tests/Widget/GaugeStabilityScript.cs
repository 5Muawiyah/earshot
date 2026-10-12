using System.Collections.Concurrent;
using System.Drawing;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Popup;
using Earshot.Tests.Streaming;
using Earshot.Widget;

namespace Earshot.Tests.Widget;

// A scripted taskbar and the clock it runs on, for tests that replay an hour of reads in a moment. The clock is a
// TestTimeProvider moved in 50 ms steps; after each step the timers it fired have posted their work (the post is a queue
// drained here, on the test's own thread, as the UI thread would) and every job that has come due is run. A job is a read,
// a signal or a change in what the taskbar shows.
internal sealed class StabilityScene
{
    public static readonly TimeSpan Step = TimeSpan.FromMilliseconds(50);

    private readonly List<Job> _jobs = [];
    private readonly ConcurrentQueue<Action> _posted = new();
    private readonly DateTimeOffset _start;

    public StabilityScene()
    {
        _start = Time.GetUtcNow();
    }

    public TestTimeProvider Time { get; } = new();

    public TimeSpan Elapsed => Time.GetUtcNow() - _start;

    // The UI-thread post the controllers and the set are given: queued, and run by Run on the next step.
    public Action<Action> Post => _posted.Enqueue;

    // Runs once, as soon as the clock is at or past this time.
    public void At(TimeSpan at, Action action) => _jobs.Add(new Job(at, TimeSpan.Zero, action));

    // Runs as soon as the next step allows, however many times it is asked for in one step.
    public void Soon(Action action) => At(Elapsed, action);

    // Runs from first on, every period.
    public void Every(TimeSpan period, TimeSpan first, Action action) => _jobs.Add(new Job(first, period, action));

    public void Run(TimeSpan duration)
    {
        TimeSpan end = Elapsed + duration;
        Drain();
        RunDueJobs();
        while (Elapsed < end)
        {
            Time.Advance(Step);
            Drain();
            RunDueJobs();
        }
    }

    private void Drain()
    {
        while (_posted.TryDequeue(out Action? action))
        {
            action();
        }
    }

    private void RunDueJobs()
    {
        bool ran;
        do
        {
            ran = false;
            TimeSpan now = Elapsed;
            for (int i = 0; i < _jobs.Count; i++)
            {
                Job job = _jobs[i];
                if (job.Done || job.Due > now)
                {
                    continue;
                }

                if (job.Period > TimeSpan.Zero)
                {
                    job.Due += job.Period;
                }
                else
                {
                    job.Done = true;
                }

                job.Action();
                Drain();
                ran = true;
            }
        }
        while (ran);

        _ = _jobs.RemoveAll(static j => j.Done);
    }

    private sealed class Job(TimeSpan due, TimeSpan period, Action action)
    {
        public TimeSpan Due { get; set; } = due;

        public TimeSpan Period { get; } = period;

        public Action Action { get; } = action;

        public bool Done { get; set; }
    }
}

// What one scripted read is asked at: which read it is, when, and where the gauge is on screen (null when it is not shown).
internal readonly record struct ScriptedRead(int Index, TimeSpan Elapsed, Rectangle? GaugeBounds);

// The taskbars the replays read, in the geometry the owner's logs showed: a 1920 x 1080 display, a 48 px bar, the
// notification area's left edge at 1678 or 28 px to the left of it.
internal static class StabilityLayouts
{
    public static readonly Rectangle Bar = new(0, 1032, 1920, 48);
    public static readonly Rectangle Start = new(762, 1032, 45, 48);
    public static readonly Rectangle Monitor = new(0, 0, 1920, 1080);

    // Where the gauge goes with the notification area at this x: eight pixels left of it, centred in the bar.
    public static Rectangle GaugeAt(int notificationX) => new(notificationX - 8 - 74, 1036, 74, 40);

    private static Rectangle Tray(int notificationX) => new(notificationX, 1032, 1920 - notificationX, 48);

    private static IEnumerable<Rectangle> Buttons() =>
        Enumerable.Range(0, 8).Select(i => new Rectangle(807 + (i * 44), 1032, 44, 48));

    // Free space between the last button and the notification area.
    public static TaskbarLayout Free(int notificationX = 1678) => From([Start, .. Buttons(), Tray(notificationX)], Tray(notificationX));

    // A button grown into the gauge's place.
    public static TaskbarLayout NoRoom(int notificationX = 1678) =>
        From([Start, .. Buttons(), new Rectangle(1159, 1032, 470, 48), Tray(notificationX)], Tray(notificationX));

    public static TaskbarLayout From(IReadOnlyList<Rectangle> occupied, Rectangle? notificationArea) =>
        new(0, Bar, TaskbarEdge.Bottom, false, Monitor, occupied, Start, 96, Shell.QUNS_ACCEPTS_NOTIFICATIONS, Covered: false, GaugeCentreIsGauge: null,
            NotificationArea: notificationArea);
}

// A GaugeController with a scripted taskbar behind it. Every read goes through OnLayout the way the watcher's result does;
// the controller's own requests for a confirming read (when it makes them) are answered on the next step.
internal sealed class ScriptedGauge
{
    private readonly Func<ScriptedRead, ITaskbarReader.Result> _taskbar;

    // answerRequests false leaves the controller's requests for a confirming read unanswered, as when the watcher delivers no read
    // but its own poll.
    public ScriptedGauge(
        StabilityScene scene, Func<ScriptedRead, ITaskbarReader.Result> taskbar, bool answerRequests = true,
        Func<GaugePosition>? position = null)
    {
        Scene = scene;
        _taskbar = taskbar;
        Position = position ?? (() => GaugePosition.RightEnd);
        Controller = GaugeStabilityApi.NewController(
            () => Surface, Icon, () => new GaugeControllerSettings(Enabled: true, LeftClickConnects: false, Position()), Log, scene.Time, scene.Post,
            requestRead: () =>
            {
                Requests++;
                if (answerRequests)
                {
                    scene.Soon(Read);
                }
            });
    }

    public Func<GaugePosition> Position { get; }

    // How often the controller asked for a confirming read.
    public int Requests { get; private set; }

    public StabilityScene Scene { get; }

    public FakeGaugeSurface Surface { get; } = new();

    public FakeTrayIcon Icon { get; } = new();

    public CapturingLog Log { get; } = new();

    public GaugeController Controller { get; }

    public int Reads { get; private set; }

    // Changes of state, counted where they happen: a gauge that was shown and is not, one that was not and is, and a window moved
    // while it stayed shown. HideWindow calls are not a count of hides (a hidden gauge is told again on every read).
    public int Hides { get; private set; }

    public int Shows { get; private set; }

    public int Moves { get; private set; }

    public List<TimeSpan> HideTimes { get; } = [];

    public List<TimeSpan> ShowTimes { get; } = [];

    public List<TimeSpan> MoveTimes { get; } = [];

    public bool IsShown => Controller.State is GaugeState.Shown;

    public Rectangle? Bounds => Controller.State is GaugeState.Shown shown ? shown.Bounds : null;

    public void Read() => Apply(() => Controller.OnLayout(_taskbar(new ScriptedRead(Reads++, Scene.Elapsed, Bounds))));

    // Runs something that may change the gauge's state, and counts what it changed.
    public void Apply(Action action)
    {
        Rectangle? before = Bounds;
        action();
        Rectangle? after = Bounds;
        if (before is not null && after is null)
        {
            Hides++;
            HideTimes.Add(Scene.Elapsed);
        }
        else if (before is null && after is not null)
        {
            Shows++;
            ShowTimes.Add(Scene.Elapsed);
        }
        else if (before is { } b && after is { } a && b != a)
        {
            Moves++;
            MoveTimes.Add(Scene.Elapsed);
        }
    }

    // A read every period from the start: the controller's own poll.
    public void Poll(TimeSpan period) => Scene.Every(period, TimeSpan.Zero, Read);

    // A read at each of these times, as a taskbar event would ask for one.
    public void ReadAt(IEnumerable<TimeSpan> times)
    {
        foreach (TimeSpan time in times)
        {
            Scene.At(time, Read);
        }
    }
}

// The gauges on the other displays' taskbars, as the owner's logs showed them: one extra display, whose taskbar window is
// there or not at the times a script says, a set that is asked to reconcile when a script says, and a count of the gauges it
// added and removed.
internal sealed class ScriptedSet : IDisposable
{
    public static readonly DisplayInfo One = Display(@"\\?\DISPLAY#AAA0001#5&1a2b3c4d&0&UID100#{monitor-interface}", @"\\.\DISPLAY1", new Rectangle(0, 0, 1920, 1080), true, 11);
    public static readonly DisplayInfo Two = Display(@"\\?\DISPLAY#AAA0001#5&1a2b3c4d&0&UID104#{monitor-interface}", @"\\.\DISPLAY2", new Rectangle(1920, 0, 1920, 1080), false, 22);

    private readonly Func<TimeSpan, bool> _taskbarSeen;
    private readonly HashSet<SecondaryGauge> _instances = [];

    public ScriptedSet(StabilityScene scene, Func<TimeSpan, bool> taskbarSeen)
    {
        Scene = scene;
        _taskbarSeen = taskbarSeen;
        Log = new StampedLog(scene);
        var parts = new SecondaryGaugeParts(
            () => new FakeTaskbarReader(),
            () => new FakeGaugeSurface(),
            () => new GaugeControllerSettings(Enabled: true, LeftClickConnects: false),
            Log,
            scene.Time,
            () => null,
            scene.Post,
            () => null,
            600000,
            new TrayIconVotes(new FakeTrayIcon()));
        Set = new SecondaryGaugeSet(parts, new ScriptedDisplays(), TaskbarsNow);
    }

    public StabilityScene Scene { get; }

    public StampedLog Log { get; }

    public SecondaryGaugeSet Set { get; }

    public int Reconciles { get; private set; }

    public int Adds => Log.Count("a gauge is added for");

    public int Removals => Log.Count("is removed (");

    public IReadOnlyList<TimeSpan> RemovalTimes => Log.Times("is removed (");

    public IReadOnlyList<TimeSpan> AddTimes => Log.Times("a gauge is added for");

    // The distinct gauge objects there have been: one for an hour in which nothing really went away.
    public int Instances => _instances.Count;

    public bool HasGauge => Set.Count > 0;

    public static DisplayInfo Display(string id, string device, Rectangle bounds, bool primary, nint handle) =>
        new(id, device, bounds, new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height - 48), primary, 96, handle);

    // What the second display's taskbar window is as the script has it now: there and visible, or there and hidden.
    private SecondaryTaskbarReading TaskbarsNow()
    {
        bool seen = _taskbarSeen(Scene.Elapsed);
        var bar = new TaskbarWindowCandidate(0x5000, new Rectangle(Two.Bounds.X, Two.Bounds.Bottom - 48, Two.Bounds.Width, 48), Visible: seen, Two.Handle);
        return new SecondaryTaskbarReading([bar]);
    }

    // The main taskbar was read: the tray matches the other displays' gauges to what is there.
    public void Reconcile()
    {
        Reconciles++;
        Set.Reconcile(wanted: true);
        foreach (SecondaryGauge gauge in Set.Gauges)
        {
            _ = _instances.Add(gauge);
        }
    }

    public void Dispose() => Set.Dispose();

    private sealed class ScriptedDisplays : IDisplaySource
    {
        public DisplayReading Read() => new([One, Two]);
    }
}

// A log that notes when, on the scene's clock, each line was written.
internal sealed class StampedLog(StabilityScene scene) : ILog
{
    private readonly Lock _gate = new();
    private readonly List<(TimeSpan At, LogLevel Level, string Message)> _lines = [];

    public IReadOnlyList<(TimeSpan At, LogLevel Level, string Message)> Lines
    {
        get
        {
            lock (_gate)
            {
                return [.. _lines];
            }
        }
    }

    public void Write(LogLevel level, string message, Exception? ex = null)
    {
        lock (_gate)
        {
            _lines.Add((scene.Elapsed, level, message));
        }
    }

    public int Count(string fragment) => Lines.Count(l => l.Message.Contains(fragment, StringComparison.Ordinal));

    public bool Has(LogLevel level, string fragment) => Lines.Any(l => l.Level == level && l.Message.Contains(fragment, StringComparison.Ordinal));

    public IReadOnlyList<TimeSpan> Times(string fragment) => [.. Lines.Where(l => l.Message.Contains(fragment, StringComparison.Ordinal)).Select(l => l.At)];
}

// The few calls the replays make on the product, in one place.
internal static class GaugeStabilityApi
{
    // The tray's main gauge: a controller that asks for a confirming read through requestRead.
    public static GaugeController NewController(
        Func<IGaugeSurface> surface, ITrayIconVisibility icon, Func<GaugeControllerSettings> settings, ILog log, TimeProvider time, Action<Action> uiPost,
        Action requestRead) =>
        new(surface, icon, settings, log, time, null, uiPost, null, requestRead);

    // The reader's per-element filter, as it runs on a real taskbar's elements.
    public static bool ReadElements(
        IUIAutomationElementArray found, Rectangle container, int ownProcessId, out List<Rectangle> occupied, out Rectangle? notificationArea, out StepOutcome? failure) =>
        UiaTaskbarReader.TryReadElements(found, container, static _ => { }, ownProcessId, out occupied, out _, out notificationArea, out failure);
}
