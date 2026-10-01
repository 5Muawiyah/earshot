using System.Globalization;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Popup;
using Earshot.Tray;
using Earshot.Update;

namespace Earshot.App;

// Repair Earshot, in the tray: the menu item and the settings row on the card both come here.
//
// One administrator prompt. While the install folder exists and grants no one but administrators write, the only program
// this elevates is the installed Earshot.exe: it is in a folder only administrators can change, which is what makes it
// a program the signed-in user cannot have swapped. The running copy is never elevated then, because it may be in a
// folder the signed-in user can write. What the tray does first is read-only: it checks every installed file against the
// SHA-256 values the installed Earshot.files.json records, and it checks the install folder's security. Then:
//
//   every file matches          the installed program repairs itself (its repair verb, or its install verb when it is
//                               older than the repair verb), re-registering the tasks, the service, the machine
//                               configuration and the device file exactly as setup does;
//   a file is missing or differs the release of the installed version is downloaded, checked against its checksum file
//                               exactly as an update's is, and handed to the installed program's update verb. That
//                               program is the one in the install folder, which only administrators can change, even
//                               when its own file is one that differs; what it installs is only the verified zip;
//   a file could not be read    nothing is elevated and nothing is changed. A file another program holds open, or one that
//                               access was refused to, says nothing about its contents, and a standard user can cause
//                               either, so the person is told to try again and the raw code is logged;
//   this copy is newer          it is not elevated either. The update path (download, SHA-256, the installed program's
//                               update verb) brings the install up to date, so Repair opens it;
//   no installed program to run (Earshot.exe is truly absent: its folder lists without it, or the folder is one a
//                               standard user can write) this copy's own setup puts a new install in place.
//
// The settings, the pinned device and the old battery set-up records (an earlier build kept them, nothing reads them now)
// are not touched by any of these: they are the same install over the same data folders that setup over an install
// already keeps.
internal sealed partial class TrayContext
{
    private Func<string, InstalledFilesReport> _checkInstalledFiles = InstalledFileCheck.Check;
    private Func<string, InstalledFile> _readInstalledFile = static path => new InstalledFileReader().Read(path);
    private UpdateOutcomeSource? _updateOutcomeSource;

    // The menu's Repair Earshot... and the card's Repair button.
    internal void RepairFromCard() => Start("repair (card)", RunRepairAsync);

    private void WireRepair(TrayStartOptions options)
    {
        _checkInstalledFiles = options.CheckInstalledFiles;
        _readInstalledFile = options.ReadInstalledFile;
        _updateOutcomeSource = options.UpdateOutcome;
        _menu.RepairClicked += (_, _) => Start("repair", RunRepairAsync);
    }

    private async Task RunRepairAsync(CardPlace place)
    {
        if (_closing)
        {
            return;
        }

        // Safe mode and test data refuse every elevated run, so nothing is checked or downloaded for one.
        string? unavailable = UpdateUnavailableReason();
        if (unavailable is not null)
        {
            _log.Info("Repair: not started. " + unavailable);
            ShowCard(TrayStatus.AppName, unavailable, place);
            return;
        }

        if (!TryBeginElevatedRun(RepairRun, place))
        {
            return;
        }

        try
        {
            await RepairAsync(place);
        }
        finally
        {
            EndElevatedRun(RepairRun);
        }
    }

    private async Task RepairAsync(CardPlace place)
    {
        InstallAssessment install = AssessInstall();
        bool runningIsNewer = BlockStatus is { RunningCopyIsNewer: true };
        InstalledFilesReport? files = null;
        InstalledFile? installedFile = null;
        if (install.State == InstallState.Usable && !runningIsNewer && _updateInstalledExe is { } installedExe)
        {
            // Reads and hashes every installed file: off the UI thread.
            string folder = Path.GetDirectoryName(installedExe) ?? installedExe;
            (files, installedFile) = await Task.Run(() => (_checkInstalledFiles(folder), _readInstalledFile(installedExe)));
            foreach (StepOutcome step in files.Steps.Where(s => !s.Ok))
            {
                _log.Warn("Repair: " + TrayReport.DescribeStep(step));
            }

            if (!installedFile.Step.Ok)
            {
                _log.Warn("Repair: the installed version was not read. " + TrayReport.DescribeStep(installedFile.Step));
            }
        }

        if (_closing)
        {
            return;
        }

        RepairPlan plan = RepairPlanner.Decide(install, runningIsNewer, files, installedFile?.Version);
        _log.Info("Repair: " + plan.Route + (plan.Route == RepairRoute.DownloadThenUpdate ? " of " + plan.Version : " (" + plan.Verb + ")") + ", because " + plan.Why + ". " + install.Detail);
        switch (plan.Route)
        {
            case RepairRoute.DownloadThenUpdate:
                await RepairByDownloadAsync(plan.Version!.Value, place);
                return;
            case RepairRoute.CouldNotRead:
                ShowCard(UpdateCopy.RepairCouldNotReadStatus, UpdateCopy.RepairCouldNotReadText, place);
                return;
            case RepairRoute.UpdateInstead:
                await CheckForUpdatesAsync(place);
                return;
            default:
                await RunRepairOperationAsync(plan.Verb, place);
                return;
        }
    }

    // The elevated run, waited for. It records how it ended in the machine folder; this tray has the result in hand, so it
    // notes that outcome as shown and the next start does not say it a second time.
    private async Task RunRepairOperationAsync(RepairVerb verb, CardPlace place)
    {
        DateTimeOffset started = _time.GetUtcNow();
        ControllerResult? result = await WithElevatedProgramAsync(() => RunOperationAsync(
            "repair", ct => _coordinator.RunAsync("repair", token => _registry.Block.RunRepairAsync(verb, token), ct), place, alwaysShowCard: true));
        if (result is { IsSuccess: true } && !_closing)
        {
            PointStartupAtInstalledCopy();
        }

        if (result is not null)
        {
            NoteRepairOutcomeShown(started);
        }
    }

    // The files of the installed version, from its release. The update path then runs from the installed program, and this
    // program closes for it as it does for an update.
    private async Task RepairByDownloadAsync(ReleaseVersion version, CardPlace place)
    {
        UpdateController? updates = EnsureUpdates();
        if (updates is null)
        {
            ShowCard(UpdateCopy.CheckFailedStatus, "Earshot cannot read its own version.", place);
            return;
        }

        ShowCard(UpdateCopy.RepairTitle, UpdateCopy.DownloadingStatus(version), place);
        WriteRepairNote(version);
        await updates.RepairAsync(version, _lifetime.Token);
        if (updates.Stage != UpdateStage.HandingOver)
        {
            DeleteRepairNote();
            await ShowUpdateResultAsync(updates.View, place);
        }
    }

    // ----- how the repair by download ended, said at the next start -----

    // The update path records its own outcome, which says "updated". A note in this user's own folder says that the update
    // was a repair, so its words are the repair's. It only changes the words: nothing is decided from it.
    private string? RepairNoteFile() =>
        _updateOutcomeSource is { } source ? Path.Combine(Path.GetDirectoryName(source.ShownFile) ?? "", "repair-note.txt") : null;

    private void WriteRepairNote(ReleaseVersion version)
    {
        string? file = RepairNoteFile();
        if (file is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, version + "\n" + _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        }
        catch (IOException ex)
        {
            _log.Warn("Repair: the note that this update is a repair could not be saved (0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + "), so the next start may say the update was an update.");
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Warn("Repair: the note that this update is a repair could not be saved (0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + "), so the next start may say the update was an update.");
        }
    }

    private void DeleteRepairNote()
    {
        string? file = RepairNoteFile();
        if (file is null)
        {
            return;
        }

        try
        {
            File.Delete(file);
        }
        catch (IOException ex)
        {
            _log.Warn("Repair: the repair note was not removed (0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + "): " + file);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Warn("Repair: the repair note was not removed (0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + "): " + file);
        }
    }

    // When the note was written, or null when there is none or it cannot be read.
    private DateTimeOffset? ReadRepairNoteTime()
    {
        string? file = RepairNoteFile();
        try
        {
            if (file is null || !File.Exists(file))
            {
                return null;
            }

            string[] lines = File.ReadAllText(file).Split('\n');
            return lines.Length >= 2 && DateTimeOffset.TryParse(lines[1].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset written)
                ? written
                : null;
        }
        catch (IOException ex)
        {
            _log.Warn("Repair: the repair note could not be read (0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + ").");
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Warn("Repair: the repair note could not be read (0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + ").");
            return null;
        }
    }

    // True when this outcome is the end of a repair by download: a note was written shortly before it.
    private bool OutcomeIsOfARepairByDownload(UpdateOutcome outcome)
    {
        if (ReadRepairNoteTime() is not { } written)
        {
            return false;
        }

        TimeSpan gap = outcome.WrittenUtc - written;
        return gap >= TimeSpan.Zero && gap <= RepairNoteWindow;
    }

    // How long after the note an outcome may come and still be the repair's. A limit chosen here (the download, the prompt
    // and the install together), not a measured figure.
    private static readonly TimeSpan RepairNoteWindow = TimeSpan.FromHours(1);

    // A repair run from the installed copy recorded how it ended, and this tray has shown that result itself.
    private void NoteRepairOutcomeShown(DateTimeOffset since)
    {
        if (_updateOutcomeSource is not { } source)
        {
            return;
        }

        GateRead<UpdateOutcome> read = new GateStore(source.MachineFolder).ReadUpdateOutcome();
        if (read.IsOk && read.Value is { Kind: UpdateOutcomeKind.Repaired or UpdateOutcomeKind.RepairFailed } outcome && outcome.WrittenUtc >= since)
        {
            NoteOutcomeShown(source.ShownFile, outcome.Id);
        }
    }
}
