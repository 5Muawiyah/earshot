using System.Globalization;
using Earshot.Contracts;

namespace Earshot.Boot.Gate;

// How the last update ended, for the tray to tell the owner. The tray has exited by the time the elevated update
// or the install it starts has anything to say, so the outcome is written to update-outcome.json in the machine
// folder (only administrators can write there, and the tray can read it) and the tray reads it once at its next
// start.
//
//   Installing: the update run checked the zip, waited for the tray and started the release's own install. Nothing
//               has said how that ended yet.
//   Installed:  that install finished.
//   Refused:    the update run stopped before it changed anything.
//   Failed:     the install started but did not finish cleanly.
//   Repaired:   a repair run from the installed copy finished.
//   RepairFailed: a repair run from the installed copy did not finish cleanly.
internal enum UpdateOutcomeKind
{
    Installing,
    Installed,
    Refused,
    Failed,
    Repaired,
    RepairFailed,
}

// Id is a fresh 32 character lower-case hex value for each outcome written, which is how the tray knows it has shown
// this one. Version is the release that was installed, or empty. Reason is plain text for the owner and Code the raw
// code of the step that failed, or empty; both are empty when nothing failed.
internal sealed record UpdateOutcome(string Id, DateTimeOffset WrittenUtc, UpdateOutcomeKind Kind, string Version, string Reason, string Code);

internal static class UpdateOutcomes
{
    // How long after the update run wrote Installing the install may still report. The install starts within a
    // few seconds and runs for less than a minute or so; this is a limit chosen for it, not a measured figure. An
    // install that reports later than this, or a setup run by hand long afterwards, does not rewrite the record.
    public static readonly TimeSpan InstallWindow = TimeSpan.FromMinutes(10);

    // The outcome of the update run itself: Installing when it handed over to the install, otherwise Refused with why.
    public static UpdateOutcome ForUpdateRun(InstallResult result, string id, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome == GateExitCode.Success)
        {
            return new UpdateOutcome(id, now, UpdateOutcomeKind.Installing, "", "", "");
        }

        (string reason, string code) = Describe(result);
        return new UpdateOutcome(id, now, UpdateOutcomeKind.Refused, "", reason, code);
    }

    // The outcome of a repair run from the installed copy: Repaired when it finished, otherwise RepairFailed with why.
    // A repair is one elevated run, so there is no earlier record for it to complete.
    public static UpdateOutcome ForRepairRun(InstallResult result, string version, string id, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(version);
        if (result.Outcome == GateExitCode.Success)
        {
            return new UpdateOutcome(id, now, UpdateOutcomeKind.Repaired, version, "", "");
        }

        (string reason, string code) = Describe(result);
        return new UpdateOutcome(id, now, UpdateOutcomeKind.RepairFailed, "", reason, code);
    }

    // The outcome of the install the update started, or null when there is nothing for it to complete: no record, one
    // that is not Installing, or one older than InstallWindow (a setup run by hand is not an update).
    public static UpdateOutcome? ForInstallRun(UpdateOutcome? current, InstallResult result, string version, string id, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (current is null || current.Kind != UpdateOutcomeKind.Installing || now - current.WrittenUtc > InstallWindow || now < current.WrittenUtc)
        {
            return null;
        }

        if (result.Outcome == GateExitCode.Success)
        {
            return new UpdateOutcome(id, now, UpdateOutcomeKind.Installed, version, "", "");
        }

        (string reason, string code) = Describe(result);
        return new UpdateOutcome(id, now, UpdateOutcomeKind.Failed, "", reason, code);
    }

    // Plain words for what stopped the run, and the raw code of the first step that did not succeed (the exit result's
    // own name when no step failed). Only text this program wrote reaches the owner, never a step's own detail, which
    // holds paths.
    internal static (string Reason, string Code) Describe(InstallResult result)
    {
        StepOutcome? failed = result.Steps.FirstOrDefault(s => !s.Ok && !(s.Step == "copy-app" && s.Detail == InstallActions.NothingToCopyDetail));
        string code = failed is not null
            ? (failed.CodeName.Length > 0 ? failed.CodeName : "0x" + unchecked((uint)failed.Code).ToString("X8", CultureInfo.InvariantCulture))
            : GateExitCodes.ResultName(result.Outcome);

        string reason = failed?.Step switch
        {
            "wait-tray" or "update-tray-still-running" => "Earshot did not close in time",
            "update-copy-zip" => "the downloaded update could not be read",
            "update-verify-zip" => "the downloaded update did not match what was checked",
            "update-unpack" => "the downloaded update did not pass its checks",
            "update-start-install" => "the installer could not be started",
            InstallRunLock.StepName => "another setup, update or repair was still running",
            "read-file-manifest" => "the installed file list is missing or damaged",
            "repair-running-from" => "the repair was not started from the installed copy",
            var step when step is not null && step.StartsWith("verify-installed", StringComparison.Ordinal) => "an installed file is missing or is not what was published",
            _ => result.Outcome switch
            {
                GateExitCode.Partial => "setup finished only in part",
                GateExitCode.FolderNotSecure => "a folder's permissions were not as Earshot needs",
                GateExitCode.NotFromInstallFolder => "Earshot was not started from its installed folder",
                GateExitCode.NotElevated => "Windows did not give the update administrator rights",
                GateExitCode.Rejected => "the update was asked for in a form Earshot did not accept",
                GateExitCode.UnsafeEnvironment => "the environment holds a setting Earshot will not update with",
                GateExitCode.NoManifest => "the update's file list is missing",
                GateExitCode.DeviceMismatch => "the paired AirPods did not match",
                _ => "a step of the update failed",
            },
        };
        return (reason, code);
    }

    // What the tray says, or null when there is nothing to say yet: an Installing record still inside its window is an
    // install that may yet report. Installing past the window never reported, and is said so without a guess at why.
    // repairByDownload: the update was a repair, which fetched the release of the version already installed and ran it
    // through the update path, so its words are the repair's.
    public static string? NoticeFor(UpdateOutcome outcome, DateTimeOffset now, bool repairByDownload = false)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        string what = repairByDownload ? "repair" : "update";
        return outcome.Kind switch
        {
            UpdateOutcomeKind.Installed when repairByDownload => "Earshot was repaired.",
            UpdateOutcomeKind.Installed => "Earshot was updated" + (outcome.Version.Length > 0 ? " to " + outcome.Version : "") + ".",
            UpdateOutcomeKind.Refused => "The " + what + " did not finish: " + outcome.Reason + ". Nothing was changed.",
            UpdateOutcomeKind.Failed => "The " + what + " did not finish: " + outcome.Reason + ". Choose Repair Earshot from its menu to repair it.",
            UpdateOutcomeKind.Repaired => "Earshot was repaired.",
            UpdateOutcomeKind.RepairFailed => "The repair did not finish: " + outcome.Reason + ". Choose Repair Earshot from its menu to try again.",
            UpdateOutcomeKind.Installing when now - outcome.WrittenUtc > InstallWindow =>
                "The " + what + " may not have finished: the installer never said. Choose Repair Earshot from its menu to repair it.",
            _ => null,
        };
    }
}
