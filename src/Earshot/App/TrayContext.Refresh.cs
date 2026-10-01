using Earshot.Widget;

namespace Earshot.App;

// Reading the battery again, from the card's icon and from the tray menu. It asks the status service to restart its
// passive listen and read Windows' own figure; nothing here connects, disconnects or changes a device.
internal sealed partial class TrayContext
{
    // What the card's refresh icon calls: the service's refresh, or "not listening" while there is no service.
    private Task<BatteryRefreshOutcome> RefreshBatteryForCard(CancellationToken ct) =>
        _widgetStatus is { } status
            ? status.RefreshBatteryAsync(ct)
            : Task.FromResult(BatteryRefreshOutcome.NotListening);

    // "Refresh battery" in the menu opens the card and starts the same refresh the icon does.
    private void OnRefreshBatteryClicked()
    {
        if (_widgetCardPresenter is not { } presenter)
        {
            return;
        }

        _caseOpenCardPresenter?.Hide();
        if (GaugeBoundsIfShown() is { } bounds)
        {
            presenter.RequestRefresh(bounds, bounds.Location);
        }
        else
        {
            presenter.RequestRefresh(gaugeBounds: null, _cursorPosition());
        }
    }

    // For tests: where the card's battery refresh stands, or null when there is no card or no refresh.
    internal BatteryRefreshView? WidgetCardRefreshViewForTest => _widgetCardPresenter?.RefreshViewForTest;
}
