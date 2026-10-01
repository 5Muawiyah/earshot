using Earshot.Contracts;

namespace Earshot.Widget;

// The battery refresh, started from the card's icon or the tray menu: the icon turns while the status service reads the
// battery again, and a refresh that ends with nothing to show says why in the read line until the card closes, a
// newer reading arrives, or another refresh starts.
internal sealed partial class WidgetCardPresenter
{
    private BatteryRefreshView? _refreshView;
    private DateTimeOffset _refreshStartedAt;
    private int _refreshFrame;
    private CancellationTokenSource? _refreshCts;
    private ITimer? _refreshSpinTimer;

    // For tests: where the refresh stands, and whether its icon is being turned.
    internal BatteryRefreshView? RefreshViewForTest => _refreshView;

    internal bool RefreshSpinnerRunningForTest => _refreshSpinTimer is not null;

    // Opens the card on the main view (if it is not showing) and starts a refresh, as the menu item does.
    public void RequestRefresh(Rectangle? gaugeBounds, Point fallbackPoint) =>
        _uiPost(() => RequestRefreshOnUiThread(gaugeBounds, fallbackPoint));

    private void RequestRefreshOnUiThread(Rectangle? gaugeBounds, Point fallbackPoint)
    {
        if (_disposed)
        {
            return;
        }

        if (_card is { IsDisposed: false, Visible: true })
        {
            if (_view == WidgetCardView.Settings)
            {
                _view = WidgetCardView.Main;
                _shortcutNote = null;
            }
        }
        else
        {
            _view = WidgetCardView.Main;
            ShowAt(gaugeBounds, fallbackPoint);
        }

        StartBatteryRefresh();
    }

    private void OnRefreshRequested(object? sender, EventArgs e) => StartBatteryRefresh();

    // One refresh at a time: a second request while one is reading joins it.
    private void StartBatteryRefresh()
    {
        if (_disposed || _callbacks.RefreshBattery is not { } refresh || _refreshView is { Reading: true })
        {
            return;
        }

        _refreshStartedAt = _time.GetUtcNow();
        _refreshFrame = 0;
        _refreshView = BatteryRefreshView.Started;
        var cts = new CancellationTokenSource();
        _refreshCts = cts;
        RenderKeepingBottom();
        SyncRefreshSpinner();

        Task<BatteryRefreshOutcome> task = refresh(cts.Token);
        _ = task.ContinueWith(
            finished => _uiPost(() => OnBatteryRefreshEnded(cts, finished)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void OnBatteryRefreshEnded(CancellationTokenSource cts, Task<BatteryRefreshOutcome> finished)
    {
        if (ReferenceEquals(_refreshCts, cts))
        {
            _refreshCts = null;
        }

        cts.Dispose();
        if (_disposed || _refreshView is not { Reading: true })
        {
            return;
        }

        if (finished.IsCompletedSuccessfully)
        {
            _refreshView = new BatteryRefreshView(Reading: false, Outcome: finished.Result);
        }
        else
        {
            // A refresh that was cancelled or failed has nothing to say; the ordinary read line stands.
            if (finished.IsFaulted)
            {
                _log.Write(LogLevel.Warn, "Widget card: battery refresh failed: " + finished.Exception?.GetBaseException().Message);
            }

            _refreshView = null;
        }

        StopRefreshSpinTimer();
        RenderKeepingBottom();
    }

    // What the card is told: the turning icon while reading, the reason a finished refresh found nothing until a newer
    // reading supersedes it, or nothing.
    private BatteryRefreshView? RefreshViewForModel()
    {
        if (_refreshView is not { } view)
        {
            return null;
        }

        if (view.Reading)
        {
            return view with { SpinFrame = _refreshFrame };
        }

        if (view.ReadLine is null)
        {
            return null;
        }

        return _callbacks.CurrentSnapshot().BatteryReadAt is { } readAt && readAt >= _refreshStartedAt ? null : view;
    }

    private void ForgetRefreshOutcome()
    {
        if (_refreshView is { Reading: false })
        {
            _refreshView = null;
        }

        StopRefreshSpinTimer();
    }

    // The icon turns only while a refresh is reading and the card is on screen.
    private void SyncRefreshSpinner()
    {
        bool spinning = _refreshView is { Reading: true } && _card is { IsDisposed: false, Visible: true };
        if (spinning && _refreshSpinTimer is null)
        {
            _refreshSpinTimer = _time.CreateTimer(_ => _uiPost(AdvanceRefreshSpinner), null, SpinnerInterval, SpinnerInterval);
        }
        else if (!spinning)
        {
            StopRefreshSpinTimer();
        }
    }

    private void AdvanceRefreshSpinner()
    {
        if (_refreshView is not { Reading: true })
        {
            return;
        }

        _refreshFrame = (_refreshFrame + 1) % BatteryRefreshView.SpinFrames;
        _card?.SetRefreshFrame(_refreshFrame);
    }

    private void StopRefreshSpinTimer()
    {
        _refreshSpinTimer?.Dispose();
        _refreshSpinTimer = null;
    }

    private void EndBatteryRefresh()
    {
        StopRefreshSpinTimer();
        _refreshCts?.Cancel();
        _refreshView = null;
    }
}
