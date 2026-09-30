using Earshot.Boot;
using System.Security.Principal;
using Earshot.Boot.Gate;
using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Popup;
using Earshot.Tray;
using Earshot.Update;

namespace Earshot.App;

// The machine folder the tray reads an update's outcome from, and the file in this user's own folder that notes which
// outcome it has shown.
internal sealed record UpdateOutcomeSource(string MachineFolder, string ShownFile);

// Check for updates, in the tray. The menu item runs a check and shows its result on the short message card the
// tray already uses. The card has no button, so Update is not offered there: the update page on the card calls
// StartUpdate. A check never downloads anything; StartUpdate is the only way a download starts, and only the
// owner's click reaches it.
//
// "Check automatically" (off by default, because a check contacts GitHub) makes one check a day, the first a little
// after startup. It only checks. It shows a card once for each version it finds, and never downloads.
//
// The hand-over closes this program, because the install replaces the install folder and cannot while this one runs from
// it: a process whose current folder is inside it, or that holds any file open in it, makes the folder's rename fail. It
// does the closing work of Exit first (the hand-back and the block before closing, with their limits), then starts the
// administrator prompt's program, then ends without another device call (PrepareHandOverAsync, in TrayContext.ElevatedRun.cs),
// because the installed Earshot.exe of 1.2.0 and of the first 1.2.1 does not wait for a copy run from another folder to end. The
// program the prompt starts is the installed Earshot.exe, which is in a folder only administrators can change, never this
// copy when this copy is somewhere else, and a newer installed version also waits for this process to end (by its id) before it
// touches that folder. So any copy can hand over once the installed program is there and its folder is administrators-only;
// with no such install the card offers Set up or Repair instead.
internal sealed partial class TrayContext
{
    private Func<IUpdateSource>? _updateSourceFactory;
    private bool _updateDataRootRedirected;
    private IUpdateLauncher? _updateLauncher;
    private UpdateController? _updates;
    private readonly Lock _updatesGate = new();
    private string? _updateInstalledExe;
    private string? _updateRunningExe;
    private Func<string, bool> _updateFileExists = File.Exists;
    private Func<string, bool> _updateFolderExists = Directory.Exists;
    private Func<string, IEnumerable<string>> _updateListFolder = static folder => Directory.EnumerateFileSystemEntries(folder);
    private IFolderSecurity _installFolderSecurity = new NtfsFolderSecurity();
    private Func<bool> _isElevated = static () => false;
    private Task? _updateAutoTask;
    private ReleaseVersion? _updateAnnounced;

    // The update controller the card's update page drives, or null when the running version cannot be read.
    internal UpdateController? Updates => EnsureUpdates();

    // True once the daily check has been started, which only happens after the setting was on.
    internal bool AutoCheckRunning => _updateAutoTask is not null;

    // Wires the two menu items and starts the daily check when the setting is already on. Called once from the
    // constructor, after the menu exists.
    private void WireUpdates(TrayStartOptions options)
    {
        _updateSourceFactory = options.UpdateSourceFactory;
        _updateLauncher = options.UpdateLauncher;
        _updateDataRootRedirected = options.DataRootRedirected;
        _updateInstalledExe = options.InstalledExePath;
        _updateRunningExe = options.ExePath;
        _updateFileExists = options.FileExists;
        _updateFolderExists = options.DirectoryExists;
        _updateListFolder = options.ListFolder;
        _elevatedExitWait = options.ElevatedExitWait;
        _installFolderSecurity = options.InstallFolderSecurity;
        _menu.CheckForUpdatesClicked += (_, _) => Start("check for updates", CheckForUpdatesAsync);
        _menu.CheckAutomaticallyClicked += (_, _) => OnCheckAutomaticallyClicked();
        WireRepair(options);
        RemoveStaleUpdateStaging();
        ApplyUpdates();
        if (options.UpdateOutcome is { } outcomeSource)
        {
            _registry.UiPost(() => ShowUpdateOutcomeOnce(outcomeSource));
        }

        // Safe mode and a redirected data folder are runs for tests and probes: never offered a switch that would exit
        // and start the owner's real install.
        if (!_registry.SafeMode && !_updateDataRootRedirected)
        {
            _registry.UiPost(OfferSwitchToInstalledCopy);
        }
    }

    // Set when the person chose to switch to the installed copy: the program to start once this one has exited. The
    // installed copy would find this one's single-instance lock still held and end at once if it were started now, so
    // Program starts it after the lock is released.
    internal string? StartAfterExit { get; private set; }

    // A copy that is not the installed one, started while an install exists, says so once on the update page of the
    // card (or a short message card when there is no card) and offers the switch. It changes nothing by itself.
    private void OfferSwitchToInstalledCopy()
    {
        if (_closing || RunsFromInstalledCopy())
        {
            return;
        }

        InstallAssessment install = AssessInstall();
        if (install.State != InstallState.Usable)
        {
            return;
        }

        _log.Info("This copy, " + _updateRunningExe + ", is not the installed one, " + _updateInstalledExe + ". It does not point Open on startup or the Start menu shortcut at itself.");

        // A switch starts the installed copy with this process's own token. From an elevated tray that would be an elevated
        // tray, which Earshot never wants running, so no switch is offered: the person is told to start it themselves.
        if (_isElevated())
        {
            _log.Info("This copy runs elevated, so no switch to the installed copy is offered: it would start elevated too.");
            ShowCard(TrayStatus.AppName, UpdateCopy.SwitchMessage, CardPlace.NearTray);
            return;
        }

        UpdateController? updates = EnsureUpdates();
        updates?.OfferSwitch();
        if (updates is null || !RequestUpdatePageFromWidget())
        {
            ShowCard(TrayStatus.AppName, UpdateCopy.SwitchMessage, CardPlace.NearTray);
        }
    }

    // The Switch button: this copy exits, in the order Exit always takes, and Program starts the installed copy, not
    // elevated, once this one has let go of its lock.
    internal void SwitchToInstalledCopy()
    {
        if (_closing)
        {
            return;
        }

        if (_isElevated())
        {
            _log.Warn("Switch: this copy runs elevated, so the installed copy would start elevated too. Nothing was started.");
            ShowCard(TrayStatus.AppName, UpdateCopy.SwitchMessage, CardPlace.NearTray);
            return;
        }

        InstallAssessment install = AssessInstall();
        if (install.State != InstallState.Usable || _updateInstalledExe is not { } installed)
        {
            _log.Warn("Switch: there is no installed copy to start (" + install.Detail + ").");
            ShowCard(TrayStatus.AppName, UpdateCopy.RepairFirstNotice, CardPlace.NearTray);
            return;
        }

        StartAfterExit = installed;
        _log.Info("Switch: this copy is closing so the installed copy, " + installed + ", can start.");
        _ = ExitAsync(CardPlace.NearTray, "Switching to the installed copy.");
    }

    // The update page's Set up button, for when there is no install to update.
    internal void SetUpFromCard() => Start("setup (card)", RunSetupAsync);

    // How the last update ended, said once, at the start after it. The elevated update has ended by then and the tray
    // that started it is gone, so the outcome is read from the machine folder, where only administrators write. It
    // is shown once: its Id is noted in a file of this user's own, and an outcome already noted is skipped. An install
    // still inside its window is left for the next start. The elevated program does not start the tray again: a
    // program it starts would be elevated too, and the tray must not run elevated. So after an update the tray comes
    // back at the next sign-in (Open on startup) or when it is started by hand, and this card is how it says what
    // happened.
    private void ShowUpdateOutcomeOnce(UpdateOutcomeSource source)
    {
        GateRead<UpdateOutcome> read = new GateStore(source.MachineFolder).ReadUpdateOutcome();
        if (read.Status == GateReadStatus.Missing)
        {
            return;
        }

        if (!read.IsOk)
        {
            _log.Warn("Update: how the last update ended could not be read. " + TrayReport.DescribeStep(read.Step));
            return;
        }

        UpdateOutcome outcome = read.Value!;
        if (LastShownOutcomeId(source.ShownFile) == outcome.Id)
        {
            return;
        }

        // An update that was a repair says so in the repair's words (the note beside the shown file, written before it).
        bool repairByDownload = OutcomeIsOfARepairByDownload(outcome);
        string? notice = UpdateOutcomes.NoticeFor(outcome, _time.GetUtcNow(), repairByDownload);
        if (notice is null)
        {
            return;
        }

        DeleteRepairNote();

        _log.Info("Update: the last update ended as " + outcome.Kind + (outcome.Code.Length > 0 ? " (" + outcome.Code + ")" : "") + ".");
        ShowCard(TrayStatus.AppName, notice, CardPlace.NearTray);
        NoteOutcomeShown(source.ShownFile, outcome.Id);
    }

    private string? LastShownOutcomeId(string file)
    {
        try
        {
            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
        catch (IOException ex)
        {
            _log.Warn("Update: the note of the last outcome shown could not be read (0x" + ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + "): " + file);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Warn("Update: the note of the last outcome shown could not be read (0x" + ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + "): " + file);
            return null;
        }
    }

    private void NoteOutcomeShown(string file, string id)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, id);
        }
        catch (IOException ex)
        {
            _log.Warn("Update: the outcome was shown but could not be noted, so it will show again (0x" + ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + "): " + file);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Warn("Update: the outcome was shown but could not be noted, so it will show again (0x" + ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + "): " + file);
        }
    }

    // The staging folders a hand-over left behind, once the program that used them is gone. Only looked for when
    // there is a staging folder at all, which is only after an update.
    private void RemoveStaleUpdateStaging()
    {
        string root = UpdateStagingRoot();
        if (Directory.Exists(root))
        {
            int removed = StagingFolders.CleanStale(root, _log);
            if (removed > 0)
            {
                _log.Info("Update: removed " + removed + " staging folder(s) an earlier update left behind.");
            }
        }
    }

    private static string UpdateStagingRoot() => Path.Combine(Paths.Current.LocalFolder, "update");

    private static string UpdateStampFile() => Path.Combine(UpdateStagingRoot(), "last-check.txt");

    // Called from the UI thread and from the daily check's own thread, so the one controller is made under a lock: two
    // callers must never each build one, or a check and an update would run on different controllers.
    private UpdateController? EnsureUpdates()
    {
        lock (_updatesGate)
        {
            if (_updates is not null)
            {
                return _updates;
            }

            ReleaseVersion? running = ReleaseVersion.Running(typeof(TrayContext).Assembly);
            if (running is null)
            {
                _log.Error("Update: this program does not report a version it can read, so it cannot check for updates.");
                return null;
            }

            ReleaseVersion installed = InstalledVersionOr(running.Value);
            IUpdateSource source = _updateSourceFactory?.Invoke() ?? new UpdateService(_log, installed, UpdateStagingRoot());
            var controller = new UpdateController(
                source, _updateLauncher ?? new ElevatedUpdateLauncher(), CurrentHandoverIdentity, CurrentHandoverTarget, UpdateUnavailableReason, installed, _log,
                () => AssessInstall().State, PrepareHandOverAsync);
            controller.HandedOver += (_, _) => _registry.UiPost(OnUpdateHandedOver);
            controller.Changed += (_, _) => RaiseCardUpdateChanged();
            _updates = controller;
            return controller;
        }
    }

    // The version an update is measured against: the one that would be replaced. That is the installed program's when this
    // copy is another one (a newer copy unzipped in a download folder must still be offered the update that brings the
    // install up to date, and an older one must not be offered the version that is installed already), and this copy's own
    // when it is the installed one or the installed version cannot be read.
    private ReleaseVersion InstalledVersionOr(ReleaseVersion running)
    {
        if (_updateInstalledExe is not { } installedExe || RunsFromInstalledCopy() || AssessInstall().State != InstallState.Usable)
        {
            return running;
        }

        InstalledFile read = _readInstalledFile(installedExe);
        if (read.Version is not { } version)
        {
            _log.Warn("Update: the installed version was not read, so updates are measured against this copy. " + TrayReport.DescribeStep(read.Step));
            return running;
        }

        return new ReleaseVersion(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));
    }

    // What is in Program Files, read without changing anything. Each call looks at the disk.
    private InstallAssessment AssessInstall() =>
        InstalledCopy.Assess(_updateInstalledExe, _updateFileExists, _updateFolderExists, _installFolderSecurity, _updateListFolder);

    // Whether this process is the installed copy.
    private bool RunsFromInstalledCopy() => InstalledCopy.SameFile(_updateInstalledExe, _updateRunningExe);

    // The installed Earshot.exe and this process's id, or null when there is no installed program to hand over to:
    // nothing is installed, or what is there is missing its program or has a folder that is not administrators-only. An
    // update installs over the installed copy and is started from it, never from this program's own folder, which may
    // be one the signed-in user can write. So whichever copy is running hands over to the installed program, and this
    // process's id is what that elevated run waits for before it touches the install folder.
    private HandoverTarget? CurrentHandoverTarget()
    {
        InstallAssessment install = AssessInstall();
        return install.State == InstallState.Usable && _updateInstalledExe is { } installed
            ? new HandoverTarget(installed, Environment.ProcessId)
            : null;
    }

    // The pinned device and the signed-in user, in the form install checks; null before setup has a device.
    private HandoverIdentity? CurrentHandoverIdentity()
    {
        string? sid;
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
        {
            sid = identity.User?.Value;
        }

        return UpdateHandover.TryIdentity(sid, _registry.Settings.Current);
    }

    // Install refuses to run in safe mode and while EARSHOT_DATA_ROOT is set (Program.PrivilegedModeRefusal), so an
    // update started in either would download and then be refused after the prompt. It says so up front instead.
    private string? UpdateUnavailableReason()
    {
        if (_registry.SafeMode)
        {
            return SafeDecorators.Message;
        }

        return _updateDataRootRedirected ? "Updating is off while Earshot uses test data." : null;
    }

    // The menu's Check for updates: a card while it checks, and a card with the result.
    private async Task CheckForUpdatesAsync(CardPlace place)
    {
        UpdateController? updates = EnsureUpdates();
        if (updates is null)
        {
            ShowCard(UpdateCopy.CheckFailedStatus, "Earshot cannot read its own version.", place);
            return;
        }

        if (!updates.IsBusy)
        {
            ShowCard(TrayStatus.AppName, UpdateCopy.CheckingStatus, place);
        }

        await updates.CheckAsync(_lifetime.Token);

        // A newer version opens the card's update page, the way "Set up battery" opens the card, so the Update button
        // is reachable from the menu. Any other result, or no card to show it on, is the short message card.
        if (updates.Stage == UpdateStage.Available && RequestUpdatePageFromWidget())
        {
            _registry.Cards.Hide();
            return;
        }

        ShowUpdateResult(updates.View, place);
    }

    // What the update page's Update button calls: download, verify and hand over, with a card for the outcome
    // unless the program is about to close for the hand-over.
    internal void StartUpdate() => Start("update", RunUpdateAsync);

    // The card's settings row runs a check like the menu's, but the result is the update page the card is already
    // showing, not a message card over it. It only checks.
    internal void CheckForUpdatesFromCard() => Start("check for updates (card)", CheckForUpdatesQuietAsync);

    private async Task CheckForUpdatesQuietAsync(CardPlace place)
    {
        UpdateController? updates = EnsureUpdates();
        if (updates is null)
        {
            return;
        }

        await updates.CheckAsync(_lifetime.Token);
    }

    // The update page's Try again: a failed check checks again, anything else is the person asking for the
    // download again, which is what Update does.
    internal void TryUpdateAgainFromCard()
    {
        if (_updates?.Stage == UpdateStage.CheckFailed)
        {
            CheckForUpdatesFromCard();
            return;
        }

        Start("update (try again)", RunTryAgainAsync);
    }

    // What Try again does after a failed download or hand-over: for an update, the same as Update; for a repair, it looks
    // for the release of the installed version again, because a repair keeps no release from an earlier check.
    private async Task RunTryAgainAsync(CardPlace place)
    {
        if (!TryBeginElevatedRun(UpdateRun, place))
        {
            return;
        }

        try
        {
            UpdateController? updates = EnsureUpdates();
            if (updates is null)
            {
                ShowCard(UpdateCopy.CheckFailedStatus, "Earshot cannot read its own version.", place);
                return;
            }

            await updates.TryAgainAsync(_lifetime.Token);
            if (updates.Stage != UpdateStage.HandingOver)
            {
                await ShowUpdateResultAsync(updates.View, place);
            }
        }
        finally
        {
            EndElevatedRun(UpdateRun);
        }
    }

    private async Task RunUpdateAsync(CardPlace place)
    {
        if (!TryBeginElevatedRun(UpdateRun, place))
        {
            return;
        }

        try
        {
            UpdateController? updates = EnsureUpdates();
            if (updates is null)
            {
                ShowCard(UpdateCopy.CheckFailedStatus, "Earshot cannot read its own version.", place);
                return;
            }

            await updates.UpdateAsync(_lifetime.Token);
            if (updates.Stage != UpdateStage.HandingOver)
            {
                await ShowUpdateResultAsync(updates.View, place);
            }
        }
        finally
        {
            EndElevatedRun(UpdateRun);
        }
    }

    private void ShowUpdateResult(UpdateViewModel view, CardPlace place) =>
        ShowCard(view.Status, view.CardText ?? "", place);

    // The result of an update or repair that did not hand over. When the tray has already done its closing device work for
    // the hand-over (the elevated program was refused or would not start after that), it cannot carry on as it was: the
    // card says what happened and the tray ends and starts again.
    private async Task ShowUpdateResultAsync(UpdateViewModel view, CardPlace place)
    {
        if (_closedForHandOver)
        {
            place.Show(_registry.Cards, view.Status, (view.CardText ?? "") + (_isElevated() ? " Start Earshot again from the Start menu." : " Earshot is starting again."));
            await ExitAfterFailedHandOverAsync();
            return;
        }

        ShowUpdateResult(view, place);
    }

    // The elevated program has started, and the tray has done its closing device work before starting it (the hand-back and
    // the block, PrepareHandOverAsync). It ends now, with no further device call: a second hand-back or block here would
    // run beside the install that is replacing this folder.
    private void OnUpdateHandedOver()
    {
        if (_closedForHandOver)
        {
            _log.Info("Update: the elevated program started, so Earshot ends.");
            ExitThread();
            return;
        }

        _ = ExitAsync(CardPlace.NearTray, "Earshot is closing so the update can replace its files.");
    }

    private void OnCheckAutomaticallyClicked()
    {
        if (_closing)
        {
            return;
        }

        CardPlace place = ClickPlace();
        bool on = !_registry.Settings.Current.CheckForUpdatesAutomatically;
        TryUpdateSettings("check for updates automatically", s => s.CheckForUpdatesAutomatically = on, place);
    }

    // Starts the daily check the first time the setting is on. The loop then reads the setting itself, so turning
    // it off stops the checks without stopping the loop, and turning it on again needs nothing more.
    private void ApplyUpdates()
    {
        if (_closed || _updateAutoTask is not null || !_registry.Settings.Current.CheckForUpdatesAutomatically)
        {
            return;
        }

        var auto = new UpdateAutoCheck(
            _time,
            () => _registry.Settings.Current.CheckForUpdatesAutomatically,
            new FileUpdateCheckStamp(UpdateStampFile(), _log),
            AutomaticCheckAsync,
            UpdateAutoCheck.StartupDelay,
            UpdateAutoCheck.PollInterval,
            _log);
        _updateAutoTask = Task.Run(() => auto.RunAsync(_lifetime.Token));
    }

    private async Task AutomaticCheckAsync(CancellationToken ct)
    {
        UpdateController? updates = EnsureUpdates();
        if (updates is null)
        {
            return;
        }

        await updates.CheckAsync(ct).ConfigureAwait(false);
        UpdateViewModel view = updates.View;
        if (view.Stage == UpdateStage.Available && updates.AvailableRelease is { } release && _updateAnnounced != release.Version)
        {
            _updateAnnounced = release.Version;
            _registry.UiPost(() => ShowCard(view.Status, view.CardText ?? "", CardPlace.NearTray));
        }
    }
}
