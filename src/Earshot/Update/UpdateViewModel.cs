namespace Earshot.Update;

// Every word the update views show, for the seven states of a check and an update and two more in the same plain
// voice: the Windows prompt could not be started, and the row before any check. Version numbers are filled in from
// the real versions, never fixed here.
internal static class UpdateCopy
{
    public const string Title = "Updates";

    // The settings row and the tray menu.
    public const string CheckRowLabel = "Check for updates";
    public const string CheckRowButton = "Check";
    public const string CheckAutomaticallyLabel = "Check automatically";

    public const string CheckingStatus = "Checking for updates";
    public const string UpToDateStatus = "You're up to date";
    public const string CheckFailedStatus = "Couldn't check for updates";
    public const string DownloadFailedStatus = "Couldn't download the update";
    public const string HandoverFailedStatus = "Couldn't start the update";
    public const string HandingOverStatus = "Approve the Windows prompt";

    public const string UpdateButton = "Update";
    public const string CancelButton = "Cancel";
    public const string TryAgainButton = "Try again";

    // What the update page offers when there is no install to hand the update to, in place of Update.
    public const string SetUpButton = "Set up Earshot";
    public const string RepairButton = "Repair Earshot";

    // Notices for an update that stayed where it was: the person's own choice, or something to do first.
    public const string PromptDeclinedNotice = "The Windows prompt was declined, so nothing was changed.";
    public const string NotPinnedNotice = "Choose your AirPods first, then update.";
    public const string SetUpFirstNotice = "Set up Earshot first, then update.";
    public const string RepairFirstNotice = "Repair Earshot first, then update.";
    public const string ClosingNotice = "Earshot is closing, so the update was not started. Nothing was changed.";
    public const string ClosingFailedNotice = "Something went wrong while Earshot closed its work, so the update was not started. Nothing was changed.";

    // A repair that fetches the installed version's release runs on the same page, in its own words.
    public const string RepairTitle = "Repair";
    public const string RepairDownloadFailedStatus = "Couldn't download the repair";
    public const string RepairHandoverFailedStatus = "Couldn't start the repair";
    public const string RepairNotPinnedReason = "Choose your AirPods first, then repair.";
    public const string RepairNoInstallReason = "There is no installed Earshot to repair.";
    public const string RepairCouldNotReadStatus = "Couldn't read the installed files";
    public const string RepairCouldNotReadText = "Earshot could not read its installed files, so nothing was changed. Try again in a moment.";
    public static string RepairingSub(ReleaseVersion version) => "Repairing " + version;

    // The card a copy shows when it is not the installed one. The installed copy is the one Windows starts from the
    // Start menu and at sign-in, so the person is offered a switch to it.
    public const string SwitchTitle = "Earshot";
    public const string SwitchStatus = "Earshot is already installed";
    public const string SwitchSub = "The installed copy is in Program Files.";
    public const string SwitchButton = "Switch to it";
    public const string SwitchMessage = "Earshot is already installed in Program Files. Start that copy from the Start menu.";

    // Said on the switch card before the person presses the button: the switch is an ordinary Exit of this copy.
    public const string SwitchNotice = "Switching closes this copy first. With Hand back on, that hands the AirPods back and blocks them.";

    public static string AvailableStatus(ReleaseVersion version) => "Version " + version + " is available";

    public static string InstalledSub(ReleaseVersion version) => "Version " + version + " installed";

    public static string CurrentSub(ReleaseVersion version) => "Version " + version;

    public static string DownloadingStatus(ReleaseVersion version) => "Downloading " + version;

    public static string InstallingSub(ReleaseVersion version) => "Installing " + version;
}

internal enum UpdateStage
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    CheckFailed,
    DownloadFailed,
    HandoverFailed,
    HandingOver,

    // This copy is not the installed one, and the person can switch to the installed one.
    SwitchOffered,
}

// The picture beside the status: a spinner, a tick, a down arrow, a caution mark or a shield.
internal enum UpdateIcon
{
    None,
    Spinner,
    Check,
    Down,
    Caution,
    Shield,
}

internal enum UpdateButtonRole
{
    Check,
    Update,
    Cancel,
    TryAgain,
    SetUp,
    Repair,
    Switch,
}

internal sealed record UpdateButton(UpdateButtonRole Role, string Label, bool Primary);

// What the update sub-page shows, with no window in it: the card renders this and calls the controller for
// each button. Status is the headline, Sub the line under it, Reason the plain cause of a failure, Notice a
// one-line note about an update that stayed where it was. ProgressPercent is set only while downloading and is
// null while the size is not known.
internal sealed record UpdateViewModel(
    UpdateStage Stage,
    string Title,
    UpdateIcon Icon,
    string Status,
    string? Sub,
    string? Reason,
    string? Notice,
    int? ProgressPercent,
    IReadOnlyList<UpdateButton> Buttons)
{
    private static readonly UpdateButton[] None = [];

    // The one line a short message card shows under its headline.
    public string? CardText => Reason ?? Notice ?? Sub;

    internal static UpdateViewModel For(
        UpdateStage stage,
        ReleaseVersion installed,
        ReleaseVersion? available,
        int? progressPercent,
        string? reason,
        string? notice,
        bool updateOffered = true,
        UpdateButtonRole? instead = null,
        bool repair = false)
    {
        string title = repair ? UpdateCopy.RepairTitle : UpdateCopy.Title;
        ReleaseVersion target = available ?? installed;
        return stage switch
        {
            UpdateStage.Idle => new(stage, title, UpdateIcon.None, UpdateCopy.CheckRowLabel, UpdateCopy.CurrentSub(installed), null, null, null,
                [new UpdateButton(UpdateButtonRole.Check, UpdateCopy.CheckRowButton, Primary: false)]),
            UpdateStage.Checking => new(stage, title, UpdateIcon.Spinner, UpdateCopy.CheckingStatus, UpdateCopy.InstalledSub(installed), null, null, null, None),
            UpdateStage.UpToDate => new(stage, title, UpdateIcon.Check, UpdateCopy.UpToDateStatus, UpdateCopy.CurrentSub(installed), null, null, null, None),
            UpdateStage.Available => new(stage, title, UpdateIcon.Down, UpdateCopy.AvailableStatus(target), UpdateCopy.InstalledSub(installed), null, notice, null,
                updateOffered ? [new UpdateButton(UpdateButtonRole.Update, UpdateCopy.UpdateButton, Primary: true)] : Instead(instead)),
            UpdateStage.Downloading => new(stage, title, UpdateIcon.Down, UpdateCopy.DownloadingStatus(target), null, null, null, progressPercent,
                [new UpdateButton(UpdateButtonRole.Cancel, UpdateCopy.CancelButton, Primary: false)]),
            UpdateStage.CheckFailed => new(stage, title, UpdateIcon.Caution, UpdateCopy.CheckFailedStatus, UpdateCopy.InstalledSub(installed), reason, null, null,
                [new UpdateButton(UpdateButtonRole.TryAgain, UpdateCopy.TryAgainButton, Primary: true)]),
            UpdateStage.DownloadFailed => new(stage, title, UpdateIcon.Caution, repair ? UpdateCopy.RepairDownloadFailedStatus : UpdateCopy.DownloadFailedStatus, UpdateCopy.CurrentSub(target), reason, null, null,
                [new UpdateButton(UpdateButtonRole.TryAgain, UpdateCopy.TryAgainButton, Primary: true)]),
            UpdateStage.HandoverFailed => new(stage, title, UpdateIcon.Caution, repair ? UpdateCopy.RepairHandoverFailedStatus : UpdateCopy.HandoverFailedStatus, UpdateCopy.CurrentSub(target), reason, null, null,
                [new UpdateButton(UpdateButtonRole.TryAgain, UpdateCopy.TryAgainButton, Primary: true)]),
            UpdateStage.HandingOver => new(stage, title, UpdateIcon.Shield, UpdateCopy.HandingOverStatus, repair ? UpdateCopy.RepairingSub(target) : UpdateCopy.InstallingSub(target), null, null, null, None),
            UpdateStage.SwitchOffered => new(stage, UpdateCopy.SwitchTitle, UpdateIcon.Shield, UpdateCopy.SwitchStatus, UpdateCopy.SwitchSub, null, UpdateCopy.SwitchNotice, null,
                [new UpdateButton(UpdateButtonRole.Switch, UpdateCopy.SwitchButton, Primary: true)]),
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Not an update stage."),
        };
    }

    // The one button that stands in for Update when there is no install to hand over to.
    private static UpdateButton[] Instead(UpdateButtonRole? role) => role switch
    {
        UpdateButtonRole.SetUp => [new UpdateButton(UpdateButtonRole.SetUp, UpdateCopy.SetUpButton, Primary: true)],
        UpdateButtonRole.Repair => [new UpdateButton(UpdateButtonRole.Repair, UpdateCopy.RepairButton, Primary: true)],
        _ => None,
    };
}
