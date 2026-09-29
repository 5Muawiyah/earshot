namespace Earshot.Widget;

// The widget's whole public surface: one snapshot, three events, and the things the owner can do from the
// UI (set up the battery, forget the claim, ask for an immediate retry). Everything else is read from Current.
public interface IWidgetStatus
{
    WidgetSnapshot Current { get; }

    // True while the watcher runs: the one thing the UI can know ahead of a set-up without guessing, so the
    // set-up trigger can be shown disabled rather than left to fail after the owner has opened his case.
    bool SetupAvailable { get; }

    event EventHandler? Changed;                          // UI thread

    event EventHandler<CaseOpenedEventArgs>? CaseOpened;  // UI thread, owned advertisements only

    event EventHandler<OwnedReadingEventArgs>? OwnedReadingApplied; // UI thread, every owned reading

    // Step 1: listens for the window and finds the owner's case. Writes nothing.
    Task<BatterySetupListen> ListenForSetupAsync(CancellationToken ct);

    // Step 3: saves the record, updates what is proved, writes the claim and applies the first reading. UI thread.
    BatterySetupResult CompleteSetup(BatterySetupListen listen, BatterySetupPicks picks);

    void ForgetClaim();

    Task RefreshAsync();                                  // one immediate watcher retry when stopped
}
