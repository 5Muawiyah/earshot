using System.Drawing;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Popup;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase5;

// A desktop for the presenter tests.
internal sealed class FakeCardEnvironment : ICardEnvironment
{
    public PlacementScene Scene { get; set; } = Desktops.BottomTaskbar();

    public int Dpi { get; set; } = 96;

    public NotificationStateReading Notifications { get; set; } = new(0, Shell.QUNS_ACCEPTS_NOTIFICATIONS);

    public CardPalette Palette { get; set; } = CardTheme.Dark;

    public int NotificationQueries { get; private set; }

    public List<DisplayArea> DpiRequests { get; } = new();

    public List<int> ThreadIds { get; } = new();

    public PlacementScene ReadScene()
    {
        ThreadIds.Add(Environment.CurrentManagedThreadId);
        return Scene;
    }

    public int DpiFor(DisplayArea display)
    {
        DpiRequests.Add(display);
        return Dpi;
    }

    public NotificationStateReading QueryNotificationState()
    {
        ThreadIds.Add(Environment.CurrentManagedThreadId);
        NotificationQueries++;
        return Notifications;
    }

    public CardPalette ReadPalette() => Palette;
}

// A card window that records what the presenter asked of it.
internal sealed class FakeCardSurface : ICardSurface
{
    public event EventHandler? Clicked;

    public bool IsDisposed { get; set; }

    public Size PreparedSize { get; set; } = new(300, 80);

    public StepOutcome ShowResult { get; set; } = StepOutcomes.FromWin32("set-window-pos:show-card", 0);

    public List<(CardContent Content, int Dpi, CardPalette Palette, int MaxWidth)> Prepared { get; } = new();

    public List<Rectangle> ShownAt { get; } = new();

    public int Hides { get; private set; }

    public bool OnScreen { get; private set; }

    public List<int> ThreadIds { get; } = new();

    public Size Prepare(CardContent content, int dpi, CardPalette palette, int maxWidth)
    {
        ThreadIds.Add(Environment.CurrentManagedThreadId);
        Prepared.Add((content, dpi, palette, maxWidth));
        return PreparedSize;
    }

    public StepOutcome ShowAt(Rectangle bounds)
    {
        ThreadIds.Add(Environment.CurrentManagedThreadId);
        ShownAt.Add(bounds);
        OnScreen = ShowResult.Ok;
        return ShowResult;
    }

    public StepOutcome? HideCard()
    {
        ThreadIds.Add(Environment.CurrentManagedThreadId);
        Hides++;
        bool wasShown = OnScreen;
        OnScreen = false;
        return wasShown ? StepOutcomes.FromWin32("set-window-pos:hide-card", 0) : null;
    }

    public void Click() => Clicked?.Invoke(this, EventArgs.Empty);

    public void Dispose() => IsDisposed = true;
}

// A dismiss timer driven by the test.
internal sealed class FakeCardTimer : ICardTimer
{
    public event EventHandler? Elapsed;

    public List<TimeSpan> Restarts { get; } = new();

    public int Stops { get; private set; }

    public bool Running { get; private set; }

    public bool IsDisposed { get; private set; }

    public void Restart(TimeSpan interval)
    {
        Restarts.Add(interval);
        Running = true;
    }

    public void Stop()
    {
        Stops++;
        Running = false;
    }

    // Ends the interval, as the real timer does when nothing restarted it.
    public void Elapse()
    {
        Assert.IsTrue(Running, "Only a running timer can elapse.");
        Running = false;
        Elapsed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => IsDisposed = true;
}

// A UI queue: Post only queues; RunAll runs everything queued, in order, on the calling thread.
internal sealed class QueuedUi
{
    private readonly Queue<Action> _queue = new();

    public int Pending
    {
        get { lock (_queue) { return _queue.Count; } }
    }

    public void Post(Action action)
    {
        lock (_queue)
        {
            _queue.Enqueue(action);
        }
    }

    public void RunAll()
    {
        while (true)
        {
            Action next;
            lock (_queue)
            {
                if (_queue.Count == 0)
                {
                    return;
                }

                next = _queue.Dequeue();
            }

            next();
        }
    }
}
