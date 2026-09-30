using Earshot.Update;
using Earshot.Widget;

namespace Earshot.App;

// The update flow's state as the card's update page draws it. This lives beside the tray, not in the widget's own
// namespace, because the widget reaches nothing of the update flow: it is handed a finished page.
internal static class WidgetCardUpdatePage
{
    public static SetupViewModel From(UpdateViewModel view, int spinnerFrame = 0)
    {
        ArgumentNullException.ThrowIfNull(view);
        SetupIcon icon = view.Icon switch
        {
            UpdateIcon.Spinner => SetupIcon.Spinner,
            UpdateIcon.Check => SetupIcon.Check,
            UpdateIcon.Down => SetupIcon.Down,
            UpdateIcon.Caution => SetupIcon.Caution,
            UpdateIcon.Shield => SetupIcon.Shield,
            _ => SetupIcon.None,
        };
        var buttons = view.Buttons.Select(b => new SetupButton(b.Label, b.Primary, b.Role switch
        {
            UpdateButtonRole.Update => SetupAction.Update,
            UpdateButtonRole.Cancel => SetupAction.Cancel,
            UpdateButtonRole.TryAgain => SetupAction.TryAgain,
            UpdateButtonRole.SetUp => SetupAction.SetUp,
            UpdateButtonRole.Repair => SetupAction.Repair,
            UpdateButtonRole.Switch => SetupAction.Switch,
            _ => SetupAction.Check,
        })).ToList();

        // A failure's cause, or a note about an update that stayed where it was, goes under the status.
        return new SetupViewModel(
            view.Title, null, null, view.Reason ?? view.Notice, icon, view.Status, view.Sub, null, buttons, spinnerFrame,
            ShowProgress: view.Stage == UpdateStage.Downloading, ProgressPercent: view.ProgressPercent);
    }

    // The page when the running version cannot be read, so there is no update flow to ask.
    public static SetupViewModel Unavailable() => new(
        UpdateCopy.Title, null, null, "Earshot cannot read its own version.", SetupIcon.Caution, UpdateCopy.CheckFailedStatus, null, null,
        Array.Empty<SetupButton>(), 0);
}
