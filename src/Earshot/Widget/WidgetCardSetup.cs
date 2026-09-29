namespace Earshot.Widget;

// Which page the card shows. Main is the three-column card; Settings is the settings page; Update is the update
// page; the rest are the steps of the battery set-up. The set-up steps and the update page are drawn from a
// SetupViewModel, since both are a status body and footer buttons on the sub-page frame.
internal enum WidgetCardView { Main, SetupListening, SetupPick, SetupDone, SetupFailed, Settings, Update }

internal enum SetupIcon { None, Spinner, Check, Caution, Down, Shield }

// Update and Check are the update page's buttons; the rest are the set-up's, and Back is any page's back button.
internal enum SetupAction { Cancel, Save, TryAgain, Done, Back, Update, Check }

internal sealed record SetupButton(string Label, bool Primary, SetupAction Action);

// Everything one set-up page draws, handed in by WidgetCardPresenter. Immutable.
internal sealed record SetupViewModel(
    string Title,            // "Set up battery"
    string? Step,            // "1/3", "2/3", "3/3", or null for a page with no step counter
    string? Prompt,          // the semibold line, or null
    string? Caption,         // the 12 px line under the prompt, or null
    SetupIcon Icon,
    string? Status,          // the line beside the icon
    string? StatusSub,
    BatterySetupPicks? Picks, // the Pick page only
    IReadOnlyList<SetupButton> Buttons,
    int SpinnerFrame,        // 0..9, advanced by the presenter's animation timer
    bool ShowProgress = false,
    int? ProgressPercent = null) // null while the size is not known: the bar shows no fill and no figure
{
    public const int SpinnerFrames = 10;

    public static SetupViewModel Listening(int spinnerFrame = 0) => new(
        WidgetCopy.SetUpBattery, "1/3", WidgetCopy.SetupOpenCase, null, SetupIcon.Spinner, WidgetCopy.SetupWaiting, null, null,
        new SetupButton[] { new(WidgetCopy.Cancel, Primary: false, SetupAction.Cancel) }, spinnerFrame);

    public static SetupViewModel Pick(BatterySetupPicks picks) => new(
        WidgetCopy.SetUpBattery, "2/3", WidgetCopy.SetupWhatDoesYourIphoneShow, WidgetCopy.SetupPickCaption, SetupIcon.None, null, null, picks,
        new SetupButton[] { new(WidgetCopy.Save, Primary: true, SetupAction.Save) }, 0);

    // The last step says what was proved, and says plainly when nothing was: never "Battery set up" for a
    // set-up that read nothing.
    public static SetupViewModel Done(BatterySetupResultStatus status) => status switch
    {
        BatterySetupResultStatus.BatterySetUp => Finished(SetupIcon.Check, WidgetCopy.SetupBatterySetUp, null),
        BatterySetupResultStatus.CaseSetUp => Finished(SetupIcon.Check, WidgetCopy.SetupCaseSetUp, WidgetCopy.SetupRepeatForBuds),
        BatterySetupResultStatus.CaseSetUpBudsSame => Finished(SetupIcon.Check, WidgetCopy.SetupCaseSetUp, WidgetCopy.SetupBudsReadTheSame),
        _ => Finished(SetupIcon.Caution, WidgetCopy.SetupCouldNotRead, WidgetCopy.SetupCapturesKept),
    };

    public static SetupViewModel Failed(BatterySetupListenStatus status) => status switch
    {
        BatterySetupListenStatus.Ambiguous => FailedWith(WidgetCopy.SetupAmbiguous, WidgetCopy.SetupAmbiguousHint),
        BatterySetupListenStatus.WatcherNotStarted => FailedWith(WidgetCopy.SetupBluetoothOff, WidgetCopy.SetupBluetoothOffHint),
        _ => FailedWith(WidgetCopy.SetupNotFound, WidgetCopy.SetupNotFoundHint),
    };

    private static SetupViewModel Finished(SetupIcon icon, string status, string? sub) => new(
        WidgetCopy.SetUpBattery, "3/3", null, null, icon, status, sub, null,
        new SetupButton[] { new(WidgetCopy.Done, Primary: true, SetupAction.Done) }, 0);

    private static SetupViewModel FailedWith(string status, string sub) => new(
        WidgetCopy.SetUpBattery, "1/3", null, null, SetupIcon.Caution, status, sub, null,
        new SetupButton[] { new(WidgetCopy.Cancel, Primary: false, SetupAction.Cancel), new(WidgetCopy.TryAgain, Primary: true, SetupAction.TryAgain) }, 0);
}
