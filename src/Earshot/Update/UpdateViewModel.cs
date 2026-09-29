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

    // Notices for an update that stayed where it was: the person's own choice, or something to do first.
    public const string PromptDeclinedNotice = "The Windows prompt was declined, so nothing was changed.";
    public const string NotPinnedNotice = "Choose your AirPods first, then update.";
    public const string SetUpFirstNotice = "Set up Earshot first, then update.";

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
        bool updateOffered = true)
    {
        string title = UpdateCopy.Title;
        ReleaseVersion target = available ?? installed;
        return stage switch
        {
            UpdateStage.Idle => new(stage, title, UpdateIcon.None, UpdateCopy.CheckRowLabel, UpdateCopy.CurrentSub(installed), null, null, null,
                [new UpdateButton(UpdateButtonRole.Check, UpdateCopy.CheckRowButton, Primary: false)]),
            UpdateStage.Checking => new(stage, title, UpdateIcon.Spinner, UpdateCopy.CheckingStatus, UpdateCopy.InstalledSub(installed), null, null, null, None),
            UpdateStage.UpToDate => new(stage, title, UpdateIcon.Check, UpdateCopy.UpToDateStatus, UpdateCopy.CurrentSub(installed), null, null, null, None),
            UpdateStage.Available => new(stage, title, UpdateIcon.Down, UpdateCopy.AvailableStatus(target), UpdateCopy.InstalledSub(installed), null, notice, null,
                updateOffered ? [new UpdateButton(UpdateButtonRole.Update, UpdateCopy.UpdateButton, Primary: true)] : None),
            UpdateStage.Downloading => new(stage, title, UpdateIcon.Down, UpdateCopy.DownloadingStatus(target), null, null, null, progressPercent,
                [new UpdateButton(UpdateButtonRole.Cancel, UpdateCopy.CancelButton, Primary: false)]),
            UpdateStage.CheckFailed => new(stage, title, UpdateIcon.Caution, UpdateCopy.CheckFailedStatus, UpdateCopy.InstalledSub(installed), reason, null, null,
                [new UpdateButton(UpdateButtonRole.TryAgain, UpdateCopy.TryAgainButton, Primary: true)]),
            UpdateStage.DownloadFailed => new(stage, title, UpdateIcon.Caution, UpdateCopy.DownloadFailedStatus, UpdateCopy.CurrentSub(target), reason, null, null,
                [new UpdateButton(UpdateButtonRole.TryAgain, UpdateCopy.TryAgainButton, Primary: true)]),
            UpdateStage.HandoverFailed => new(stage, title, UpdateIcon.Caution, UpdateCopy.HandoverFailedStatus, UpdateCopy.CurrentSub(target), reason, null, null,
                [new UpdateButton(UpdateButtonRole.TryAgain, UpdateCopy.TryAgainButton, Primary: true)]),
            UpdateStage.HandingOver => new(stage, title, UpdateIcon.Shield, UpdateCopy.HandingOverStatus, UpdateCopy.InstallingSub(target), null, null, null, None),
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Not an update stage."),
        };
    }
}
