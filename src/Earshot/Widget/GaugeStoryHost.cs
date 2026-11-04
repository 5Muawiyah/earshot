namespace Earshot.Widget;

// Keeps the gauge's story (GaugeStory) for the tray: toggles start and end here, a card says when it is on screen, and the
// gauge asks what to show. It owns the one timer there is, the end of a tick's or a cross's hold, and none for a spin.
//
// The gauge shows the story only while no card is on screen: a card tells a toggle itself. Closing the card with a toggle still
// running hands the story to the gauge at once; a result that arrived while a card was on screen is the card's and is not shown
// on the gauge afterwards. The story's state is kept whether or not the gauge is drawn, so a gauge hidden for a full-screen
// application draws nothing and, shown again, draws what is true then.
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

    // The toggle that started has ended; always called, from the toggle's own finally.
    public void ToggleEnded(ToggleOutcome outcome)
    {
        if (_disposed)
        {
            return;
        }

        GaugeStoryView before = Current;
        StopTimer();
        DateTimeOffset now = _time.GetUtcNow();
        _story.Ended(outcome, now);

        // A hold that begins with a card on screen is the card's to show.
        _holdIsTheCards = _cardOnScreen;
        if (_story.NextChange is { } due)
        {
            long generation = ++_generation;
            _holdTimer = _time.CreateTimer(_ => _uiPost(() => OnHoldEnded(generation)), null, due - now, Timeout.InfiniteTimeSpan);
        }

        RaiseIfChanged(before);
    }

    // A card (asked, unasked or compact) came on screen or left it. Recalculated at once, so closing a card hands over at once.
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
