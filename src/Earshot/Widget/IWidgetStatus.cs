namespace Earshot.Widget;

// The widget's whole public surface: one snapshot, three events, an immediate retry of the watcher and a battery
// refresh. Everything else is read from Current.
public interface IWidgetStatus
{
    WidgetSnapshot Current { get; }

    // True while the watcher runs: the one thing the UI can know ahead of a refresh, so its trigger can be shown
    // disabled with the reason rather than left to fail.
    bool BatteryRefreshAvailable { get; }

    event EventHandler? Changed;                          // UI thread

    event EventHandler<CaseOpenedEventArgs>? CaseOpened;  // UI thread; the lid is not read, so never raised today

    event EventHandler<ReadingAppliedEventArgs>? ReadingApplied; // UI thread, every reading of the chosen set

    Task RefreshAsync();                                  // one immediate watcher retry when stopped

    // Restarts the passive listen, starts a read of Windows' own Hands-Free figure, and waits for a message of the
    // chosen set after the restart or for the refresh window to pass. A second request while one runs joins it. No
    // value is cleared by a refresh. Cancelling stops only the caller's wait, not the refresh.
    Task<BatteryRefreshOutcome> RefreshBatteryAsync(CancellationToken ct);
}
