using System.Drawing;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase5;

// A desktop for the presenter tests.
internal sealed class FakeCardEnvironment : ICardEnvironment
{
    public PlacementScene Scene { get; set; } = Desktops.BottomTaskbar();

    public int Dpi { get; set; } = 96;

    public NotificationStateReading Notifications { get; set; } = new(0, Shell.QUNS_ACCEPTS_NOTIFICATIONS);

    // Set to make the query of the notification state throw, as a call into the shell that fails badly can.
    public Exception? NotificationFailure { get; set; }

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
        if (NotificationFailure is { } failure)
        {
            throw failure;
        }

        return Notifications;
    }

    public CardPalette ReadPalette() => Palette;
}

// A clock that moves only when the test advances it.
internal sealed class CardClock : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public void Advance(TimeSpan by) => _ticks += by.Ticks;
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
