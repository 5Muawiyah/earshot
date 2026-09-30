using Earshot.Contracts;

namespace Earshot.Update;

// The update flow with no window in it: check, then on the person's click download, verify and hand over to Windows'
// administrator prompt. It holds the state the update sub-page shows (View) and raises Changed when it moves.
//
// Nothing is downloaded or installed until UpdateAsync is called, and only the person's own click calls it. A check
// never downloads. The hand-over starts the installed, administrator-owned Earshot.exe elevated with the update verb
// and the downloaded zip with the hash it matched; that program checks the zip again from a copy only administrators
// can write, against the hash on its own command line. That closes the gap between this program's check and the
// install, but it does not stop a program the signed-in user runs from raising the same prompt with a zip of its own
// and that zip's own hash, an older release included: the hash is only as trusted as the command line it came on, and
// the person is the one who accepts or declines the prompt (Program.Update.cs says the same). Once it has started,
// HandedOver is raised and the caller ends this program, because the install replaces the folder this one runs from and the
// elevated program waits for this one to end first.
//
// Methods may be called from any thread; the state is guarded, and events are raised outside the guard.
internal sealed class UpdateController
{
    private readonly IUpdateSource _source;
    private readonly IUpdateLauncher _launcher;
    private readonly Func<HandoverIdentity?> _identity;
    private readonly Func<HandoverTarget?> _target;
    private readonly Func<string?> _unavailable;
    private readonly ReleaseVersion _installed;
    private readonly ILog _log;
    private readonly Lock _gate = new();

    private UpdateStage _stage = UpdateStage.Idle;
    private ReleaseInfo? _release;
    private ReleaseVersion? _latest;
    private int? _percent;
    private string? _reason;
    private string? _notice;
    private bool _updateOffered = true;
    private UpdateButtonRole? _instead;
    private CancellationTokenSource? _download;
    private readonly Func<InstallState>? _installState;

    // identity: the pinned device install needs, or null before setup has one. target: the installed program to hand
    // over to, or null when there is none to hand over to (nothing installed, or an install that cannot be used). This
    // program need not be that install: the elevated run is always the installed program, and this program's own
    // process id is what it waits on. unavailable: why an update cannot run in this process at all (safe mode, test
    // data), or null. installState: what to say when target is null, Set up when nothing is installed and Repair when
    // something is but cannot be used; Set up when it is not given.
    public UpdateController(
        IUpdateSource source, IUpdateLauncher launcher, Func<HandoverIdentity?> identity, Func<HandoverTarget?> target, Func<string?> unavailable,
        ReleaseVersion installed, ILog log, Func<InstallState>? installState = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(unavailable);
        ArgumentNullException.ThrowIfNull(log);
        _source = source;
        _launcher = launcher;
        _identity = identity;
        _target = target;
        _unavailable = unavailable;
        _installed = installed;
        _log = log;
        _installState = installState;
    }

    public event EventHandler? Changed;

    // Raised once the elevated program has started. The caller ends the program.
    public event EventHandler? HandedOver;

    public ReleaseVersion Installed => _installed;

    public UpdateViewModel View
    {
        get
        {
            lock (_gate)
            {
                return UpdateViewModel.For(_stage, _installed, _release?.Version ?? _latest, _percent, _reason, _notice, _updateOffered, _instead);
            }
        }
    }

    public UpdateStage Stage
    {
        get
        {
            lock (_gate)
            {
                return _stage;
            }
        }
    }

    // A check, a download or a hand-over is under way.
    public bool IsBusy => Stage is UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.HandingOver;

    // The release a check found, until it is dealt with.
    public ReleaseInfo? AvailableRelease
    {
        get
        {
            lock (_gate)
            {
                return _release;
            }
        }
    }

    // Reads the feed and moves to up to date, update available or check failed. A cancel (the program closing)
    // returns to idle without a word.
    public async Task CheckAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_stage is UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.HandingOver)
            {
                return;
            }

            _stage = UpdateStage.Checking;
            _release = null;
            _latest = null;
            _reason = null;
            _notice = null;
            _percent = null;
        }

        RaiseChanged();
        UpdateCheckResult result;
        try
        {
            result = await _source.CheckAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Move(UpdateStage.Idle);
            return;
        }
        catch (Exception ex)
        {
            // An exception the source did not turn into a failure is still shown, with its type in the log: the
            // view never stays on "Checking" for something that has ended.
            _log.Error("Update check: unexpected " + ex.GetType().Name + ".", ex);
            result = UpdateCheckResult.Failed(new UpdateFailure(UpdateFailureKind.Network, "Something went wrong while checking.", ex.GetType().Name + ": " + ex.Message));
        }

        // Read before the guard is taken: it looks at the disk.
        bool installedCopy = _target() is not null;
        (string Notice, UpdateButtonRole Button) missing = installedCopy ? default : MissingInstall();
        lock (_gate)
        {
            _updateOffered = true;
            _instead = null;
            switch (result.Outcome)
            {
                case UpdateCheckOutcome.UpToDate:
                    _stage = UpdateStage.UpToDate;
                    _latest = result.Latest;
                    break;
                case UpdateCheckOutcome.Available:
                    _stage = UpdateStage.Available;
                    _release = result.Release;
                    _latest = result.Latest;
                    if (!installedCopy)
                    {
                        // The update installs over the installed copy and is started from it, so with no install to
                        // hand over to there is no Update: the same card offers the way to get one.
                        _updateOffered = false;
                        _notice = missing.Notice;
                        _instead = missing.Button;
                    }

                    break;
                default:
                    _stage = result.Failure?.Kind == UpdateFailureKind.Cancelled ? UpdateStage.Idle : UpdateStage.CheckFailed;
                    _reason = result.Failure?.Reason;
                    break;
            }
        }

        RaiseChanged();
    }

    // The person clicked Update: download, verify, then hand over. From "available", or from a failure of the download
    // or the hand-over (Try again). Anything else is ignored.
    public async Task UpdateAsync(CancellationToken ct)
    {
        // Read before the guard is taken: neither call belongs under it.
        string? unavailable = _unavailable();
        bool pinned = _identity() is not null;
        bool installedCopy = _target() is not null;
        (string Notice, UpdateButtonRole Button) missing = installedCopy ? default : MissingInstall();
        ReleaseInfo release;
        CancellationTokenSource? cancel = null;
        lock (_gate)
        {
            if (_stage is not (UpdateStage.Available or UpdateStage.DownloadFailed or UpdateStage.HandoverFailed) || _release is null)
            {
                return;
            }

            release = _release;
            _reason = null;
            if (unavailable is not null)
            {
                _stage = UpdateStage.Available;
                _notice = unavailable;
                _log.Info("Update: not started. " + unavailable);
            }
            else if (!installedCopy)
            {
                _stage = UpdateStage.Available;
                _notice = missing.Notice;
                _instead = missing.Button;
                _updateOffered = false;
                _log.Info("Update: not started. There is no installed copy to hand over to (" + missing.Button + ").");
            }
            else if (!pinned)
            {
                _stage = UpdateStage.Available;
                _notice = UpdateCopy.NotPinnedNotice;
                _log.Info("Update: not started. No device is pinned, and setup needs one.");
            }
            else
            {
                _stage = UpdateStage.Downloading;
                _percent = null;
                _notice = null;
                cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _download = cancel;
            }
        }

        if (cancel is null)
        {
            RaiseChanged();
            return;
        }

        RaiseChanged();
        UpdateDownloadResult download;
        try
        {
            download = await _source.DownloadAsync(release, new Progress(this), cancel.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error("Update download: unexpected " + ex.GetType().Name + ".", ex);
            download = UpdateDownloadResult.Failed(new UpdateFailure(UpdateFailureKind.Network, "Something went wrong while downloading.", ex.GetType().Name + ": " + ex.Message));
        }
        catch (OperationCanceledException)
        {
            download = UpdateDownloadResult.Failed(new UpdateFailure(UpdateFailureKind.Cancelled, UpdateService.ReasonFor(UpdateFailureKind.Cancelled), "The download was cancelled."));
        }
        finally
        {
            // Forgotten before it is disposed, so a Cancel that arrives now never meets a disposed source.
            lock (_gate)
            {
                _download = null;
            }

            cancel.Dispose();
        }

        if (download.Staged is null)
        {
            lock (_gate)
            {
                bool cancelled = download.Failure?.Kind == UpdateFailureKind.Cancelled;
                _stage = cancelled ? UpdateStage.Available : UpdateStage.DownloadFailed;
                _reason = cancelled ? null : download.Failure?.Reason;
                _percent = null;
            }

            RaiseChanged();
            return;
        }

        HandOver(download.Staged);
    }

    // Stops a download under way, and returns to "available". Anything else is ignored.
    public void Cancel()
    {
        CancellationTokenSource? running;
        lock (_gate)
        {
            running = _stage == UpdateStage.Downloading ? _download : null;
        }

        running?.Cancel();
    }

    // The button after a failure: check again after a failed check, download again after a failed download or
    // hand-over.
    public Task TryAgainAsync(CancellationToken ct) =>
        Stage == UpdateStage.CheckFailed ? CheckAsync(ct) : UpdateAsync(ct);

    private void HandOver(StagedUpdate staged)
    {
        HandoverIdentity? identity = _identity();
        HandoverTarget? target = _target();
        Move(UpdateStage.HandingOver);
        if (identity is null || target is null)
        {
            // Settings, or the install, changed under the download. Nothing has been handed over.
            staged.Discard();
            if (identity is null)
            {
                Fail(UpdateStage.Available, null, UpdateCopy.NotPinnedNotice);
                return;
            }

            (string notice, UpdateButtonRole button) = MissingInstall();
            lock (_gate)
            {
                _updateOffered = false;
                _instead = button;
            }

            Fail(UpdateStage.Available, null, notice);
            return;
        }

        LaunchResult launch;
        try
        {
            // The installed program, from the system folder: the elevated program must not have the install folder as
            // its current folder, because it ends before the install replaces that folder.
            launch = _launcher.Launch(
                target.InstalledExecutable, UpdateHandover.UpdateArguments(staged.ZipPath, staged.ZipSha256, target.TrayProcessId, identity),
                Environment.SystemDirectory);
        }
        catch (Exception ex)
        {
            // A launcher that throws instead of answering must not leave the view on "Approve the Windows prompt".
            _log.Error("Update: starting the installed program threw " + ex.GetType().Name + ".", ex);
            launch = new LaunchResult(LaunchOutcome.Failed, 0, ex.GetType().Name + ": " + ex.Message, null);
        }

        if (launch.Outcome == LaunchOutcome.Started)
        {
            _log.Info("Update: started " + launch.Detail + ". Earshot ends so the new files can replace its own.");
            launch.Process?.Dispose();

            // The staging folder stays: the elevated program reads the zip from it. The next start removes it.
            HandedOver?.Invoke(this, EventArgs.Empty);
            return;
        }

        staged.Discard();
        if (launch.Outcome == LaunchOutcome.Declined)
        {
            _log.Info("Update: the Windows prompt was declined (" + launch.Detail + "). Nothing was changed.");
            Fail(UpdateStage.Available, null, UpdateCopy.PromptDeclinedNotice);
            return;
        }

        _log.Warn("Update: the installed program did not start (" + launch.Detail + "). Nothing was changed.");
        Fail(UpdateStage.HandoverFailed, "Windows would not start the update (error " + launch.Win32Error + "). Nothing was changed.", null);
    }

    // What to say, and which button to offer, when there is no install to hand over to. Looks at the disk, so it is
    // never called under the guard.
    private (string Notice, UpdateButtonRole Button) MissingInstall() =>
        (_installState?.Invoke() ?? InstallState.Nothing) == InstallState.Unusable
            ? (UpdateCopy.RepairFirstNotice, UpdateButtonRole.Repair)
            : (UpdateCopy.SetUpFirstNotice, UpdateButtonRole.SetUp);

    // This copy is not the installed one: the page offers a switch to the installed copy. Only from idle: a check or an
    // update the person has already started is not replaced by it.
    public void OfferSwitch()
    {
        lock (_gate)
        {
            if (_stage != UpdateStage.Idle)
            {
                return;
            }

            _stage = UpdateStage.SwitchOffered;
            _release = null;
            _reason = null;
            _notice = null;
            _percent = null;
        }

        RaiseChanged();
    }

    private void Fail(UpdateStage stage, string? reason, string? notice)
    {
        lock (_gate)
        {
            _stage = stage;
            _reason = reason;
            _notice = notice;
            _percent = null;
        }

        RaiseChanged();
    }

    private void Move(UpdateStage stage)
    {
        lock (_gate)
        {
            _stage = stage;
        }

        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private void SetPercent(int? percent)
    {
        lock (_gate)
        {
            if (_stage != UpdateStage.Downloading || _percent == percent)
            {
                return;
            }

            _percent = percent;
        }

        RaiseChanged();
    }

    // Progress reports come from the download's own thread.
    private sealed class Progress(UpdateController owner) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value) => owner.SetPercent(value.Percent);
    }
}
