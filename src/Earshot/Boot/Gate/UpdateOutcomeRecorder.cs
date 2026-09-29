using Earshot.Contracts;

namespace Earshot.Boot.Gate;

// Writes how an update ended (UpdateOutcome) into the machine folder, from the two elevated runs an update is made of:
// the update run, and the install the release it unpacked runs. Nothing is written unless the machine folder reads back
// as hardened, as for the log beside it, and the folder is never created here. A write that cannot be made is logged
// with its raw code and never stops the update: the update's own log still holds the whole story.
internal sealed class UpdateOutcomeRecorder
{
    private readonly GateStore _store;
    private readonly IFolderSecurity _folders;
    private readonly ILog _log;
    private readonly TimeProvider _time;

    public UpdateOutcomeRecorder(string machineFolder, IFolderSecurity folders, ILog log, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineFolder);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(time);
        _store = new GateStore(machineFolder);
        _folders = folders;
        _log = log;
        _time = time;
    }

    // After the update run: Installing when it started the install, Refused when it stopped first.
    public void RecordUpdateRun(InstallResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!FolderIsTrusted())
        {
            return;
        }

        Write(UpdateOutcomes.ForUpdateRun(result, NewId(), _time.GetUtcNow()));
    }

    // After the install run: completes an Installing record the update run wrote a moment ago, and does nothing for any
    // other install (a first setup, a repair, a setup run by hand). version is the release this program is.
    public void RecordInstallRun(InstallResult result, string version)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(version);
        if (!FolderIsTrusted())
        {
            return;
        }

        GateRead<UpdateOutcome> current = _store.ReadUpdateOutcome();
        if (current.Status is GateReadStatus.Invalid or GateReadStatus.Unreadable)
        {
            _log.Warn("update outcome: " + Describe(current.Step));
        }

        UpdateOutcome? next = UpdateOutcomes.ForInstallRun(current.Value, result, version, NewId(), _time.GetUtcNow());
        if (next is not null)
        {
            Write(next);
        }
    }

    private void Write(UpdateOutcome outcome)
    {
        StepOutcome written = _store.WriteUpdateOutcome(outcome);
        if (written.Ok)
        {
            _log.Info("update outcome: recorded " + outcome.Kind + ".");
        }
        else
        {
            _log.Warn("update outcome: " + outcome.Kind + " could not be recorded. " + Describe(written));
        }
    }

    private bool FolderIsTrusted()
    {
        StepOutcome read = _folders.ReadSddl(_store.Folder, out string? sddl);
        if (!read.Ok)
        {
            _log.Warn("update outcome: not recorded, the machine folder could not be checked. " + Describe(read));
            return false;
        }

        if (AclCheck.CheckMachineFolder(sddl).Count > 0)
        {
            _log.Warn("update outcome: not recorded, the machine folder did not pass its check.");
            return false;
        }

        return true;
    }

    private static string Describe(StepOutcome step) =>
        step.Step + " " + step.CodeName + (string.IsNullOrEmpty(step.Detail) ? "" : ": " + step.Detail);

    private static string NewId() => Guid.NewGuid().ToString("N");
}
