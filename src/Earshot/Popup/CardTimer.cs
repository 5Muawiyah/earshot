namespace Earshot.Popup;

// The card's dismiss timer. The WinForms implementation is below; tests supply their own.
internal interface ICardTimer : IDisposable
{
    // Raised once per Restart, when the interval has passed without another Restart or Stop.
    event EventHandler? Elapsed;

    // Starts the interval again from now.
    void Restart(TimeSpan interval);

    void Stop();
}

// A System.Windows.Forms.Timer: it ticks on the thread that created it, which is the UI thread, so the
// presenter needs no locking.
// https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.timer
internal sealed class FormsCardTimer : ICardTimer
{
    private readonly System.Windows.Forms.Timer _timer = new();

    public FormsCardTimer()
    {
        _timer.Tick += OnTick;
    }

    public event EventHandler? Elapsed;

    public void Restart(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        _timer.Stop();
        _timer.Interval = (int)Math.Min(int.MaxValue, Math.Ceiling(interval.TotalMilliseconds));
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer.Dispose();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        Elapsed?.Invoke(this, EventArgs.Empty);
    }
}
