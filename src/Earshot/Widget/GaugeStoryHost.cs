namespace Earshot.Widget;

// Keeps the gauge's story (GaugeStory) for the tray: toggles start and end here, the tray says when a full card is on screen, and the
// gauge asks what to show. It owns the one timer there is, the end of a tick's or a cross's hold, and none for a spin.
//
// The gauge shows the story only while no card is on screen: a card tells a toggle itself. Closing the card with a toggle still
// running hands the story to the gauge at once; a result that arrived while a card was on screen is the card's and is not shown
// on the gauge afterwards. The story's state is kept whether or not the gauge is drawn, so a gauge hidden for a full-screen
// application draws nothing and, shown again, draws what is true then. The one exception is a cross that has to be seen (a disconnect
// whose block did not take): it has no hold to run out until a gauge on screen has drawn it, so a presentation cannot swallow it.
//
// UI thread only; the timer posts back through uiPost.
internal sealed class GaugeStoryHost : IDisposable
{
    private readonly TimeProvider _time;
    private readonly Action<Action> _uiPost;
    private readonly GaugeStory _story = new();
    private ITimer? _holdTimer;
    private long _generation;
    private bool _cardOnScreen;
    private bool _holdIsTheCards;
    private bool _disposed;

    public GaugeStoryHost(TimeProvider time, Action<Action> uiPost)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(uiPost);
        _time = time;
        _uiPost = uiPost;
    }

    // What the gauge shows of the story now has moved: draw it again.
    public event Action? Changed;

    // What the gauge shows now: nothing while a card is on screen.
    public GaugeStoryView Current => _cardOnScreen || _holdIsTheCards ? default : _story.View(_time.GetUtcNow());

    // Where the toggle being told was started from.
    public ToggleOrigin Origin => _story.Origin;

    // A connect or disconnect started, in the direction wanted.
    public void ToggleStarted(bool wanted, ToggleOrigin origin)
    {
        if (_disposed)
        {
            return;
        }

        GaugeStoryView before = Current;
        StopTimer();
        _holdIsTheCards = false;
        _story.Started(wanted, origin, _time.GetUtcNow());
        RaiseIfChanged(before);
    }

    // The toggle that started has ended; always called, from the toggle's own finally. mustBeSeen: the cross of this failure has no end
    // until a gauge on screen has drawn it (GaugeDrawn), so a gauge hidden for the whole of its hold does not lose it.
    public void ToggleEnded(ToggleOutcome outcome, bool mustBeSeen = false)
    {
        if (_disposed)
        {
            return;
        }

        GaugeStoryView before = Current;
        StopTimer();
        DateTimeOffset now = _time.GetUtcNow();
        _story.Ended(outcome, now, mustBeSeen);

        // A hold that begins with a card on screen is the card's to show. A cross still waiting to be seen has no hold to run out, so it
        // is not the card's: it shows the moment no card is on screen.
        _holdIsTheCards = _cardOnScreen && !_story.AwaitingSight;
        ArmTimer(now);
        RaiseIfChanged(before);
    }

    // A cross that has to be seen is owed: it is on the story, shown by no gauge yet.
    public bool CrossIsOwed => _story.AwaitingSight;

    // Raised once, when a gauge on screen has drawn the cross that was owed.
    public event Action? CrossSeen;

    // A gauge that is on screen has just drawn the story. The first time that is the cross that was owed, its hold begins.
    public void GaugeDrawn()
    {
        if (_disposed || !_story.AwaitingSight || Current.Kind != GaugeStoryKind.Cross)
        {
            return;
        }

        DateTimeOffset now = _time.GetUtcNow();
        _story.Seen(now);
        StopTimer();
        ArmTimer(now);
        CrossSeen?.Invoke();
    }

    // The cross that was owed has been said another way (a card shows the failure) or is over (the AirPods connected again, a later
    // block took): it goes without being drawn.
    public void DismissOwedCross()
    {
        if (_disposed)
        {
            return;
        }

        GaugeStoryView before = Current;
        if (_story.Dismiss())
        {
            RaiseIfChanged(before);
        }
    }

    private void ArmTimer(DateTimeOffset now)
    {
        if (_story.NextChange is { } due)
        {
            long generation = ++_generation;
            _holdTimer = _time.CreateTimer(_ => _uiPost(() => OnHoldEnded(generation)), null, due - now, Timeout.InfiniteTimeSpan);
        }
    }

    // A full card (the asked one, or an unasked one that is not the compact row) came on screen or left it; a compact row does not count,
    // since it says nothing of a toggle. Recalculated at once, so closing a card hands over at once.
    public void SetCardOnScreen(bool onScreen)
    {
        if (_disposed || _cardOnScreen == onScreen)
        {
            return;
        }

        GaugeStoryView before = Current;
        _cardOnScreen = onScreen;
        RaiseIfChanged(before);
    }

    public void Dispose()
    {
        _disposed = true;
        StopTimer();
    }

    private void OnHoldEnded(long generation)
    {
        if (_disposed || generation != _generation)
        {
            return;
        }

        StopTimer();
        _holdIsTheCards = false;
        Changed?.Invoke();
    }

    private void RaiseIfChanged(GaugeStoryView before)
    {
        if (Current != before)
        {
            Changed?.Invoke();
        }
    }

    private void StopTimer()
    {
        _generation++;
        _holdTimer?.Dispose();
        _holdTimer = null;
    }
}
