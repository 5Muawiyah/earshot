namespace Earshot.Widget;

// The widget's whole public surface: one snapshot, three events, and an immediate retry of the watcher.
// Everything else is read from Current.
public interface IWidgetStatus
{
    WidgetSnapshot Current { get; }

    event EventHandler? Changed;                          // UI thread

    event EventHandler<CaseOpenedEventArgs>? CaseOpened;  // UI thread; the lid is not read, so never raised today

    event EventHandler<ReadingAppliedEventArgs>? ReadingApplied; // UI thread, every reading of the chosen set

    Task RefreshAsync();                                  // one immediate watcher retry when stopped
}
