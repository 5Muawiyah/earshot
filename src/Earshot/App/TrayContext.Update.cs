using System.Security.Principal;
using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Popup;
using Earshot.Tray;
using Earshot.Update;

namespace Earshot.App;

// Check for updates, in the tray. The menu item runs a check and shows its result on the short message card the
// tray already uses. The card has no button, so Update is not offered there: the update page on the card calls
// StartUpdate. A check never downloads anything; StartUpdate is the only way a download starts, and only the
// owner's click reaches it.
//
// "Check automatically" (off by default, because a check contacts GitHub) makes one check a day, the first a little
// after startup. It only checks. It shows a card once for each version it finds, and never downloads.
//
// When the hand-over has started the administrator prompt's program, this program closes through the same orderly
// Exit as the menu's, because the install replaces the install folder and cannot while this one runs from it: a
// process whose current folder is inside it, or that holds any file open in it, makes the folder's rename fail. The
// program the prompt starts is the installed Earshot.exe, and it waits for this process to end (by its id) before it
// touches that folder. So Update is only offered to the tray that is the installed copy.
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
        _menu.CheckForUpdatesClicked += (_, _) => Start("check for updates", CheckForUpdatesAsync);
        _menu.CheckAutomaticallyClicked += (_, _) => OnCheckAutomaticallyClicked();
        RemoveStaleUpdateStaging();
        ApplyUpdates();
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

            IUpdateSource source = _updateSourceFactory?.Invoke() ?? new UpdateService(_log, running.Value, UpdateStagingRoot());
            var controller = new UpdateController(
                source, _updateLauncher ?? new ElevatedUpdateLauncher(), CurrentHandoverIdentity, CurrentHandoverTarget, UpdateUnavailableReason, running.Value, _log);
            controller.HandedOver += (_, _) => _registry.UiPost(OnUpdateHandedOver);
            controller.Changed += (_, _) => RaiseCardUpdateChanged();
            _updates = controller;
            return controller;
        }
    }

    // The installed Earshot.exe and this process's id, or null when this program is not the installed copy: not set up
    // (no file at the installed path), or started from another folder. An update installs over the installed copy and
    // is started from it, so only the tray that runs from it can hand over.
    private HandoverTarget? CurrentHandoverTarget()
    {
        string? installed = _updateInstalledExe;
        string? running = _updateRunningExe;
        if (installed is null || running is null || !_updateFileExists(installed))
        {
            return null;
        }

        return string.Equals(Path.GetFullPath(installed), Path.GetFullPath(running), StringComparison.OrdinalIgnoreCase)
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

        StartUpdate();
    }

    private async Task RunUpdateAsync(CardPlace place)
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
            ShowUpdateResult(updates.View, place);
        }
    }

    private void ShowUpdateResult(UpdateViewModel view, CardPlace place) =>
        ShowCard(view.Status, view.CardText ?? "", place);

    // The elevated program has started. Close through Exit: it blocks the AirPods at rest and lets go of every file.
    private void OnUpdateHandedOver() =>
        _ = ExitAsync(CardPlace.NearTray, "Earshot is closing so the update can replace its files.");

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
