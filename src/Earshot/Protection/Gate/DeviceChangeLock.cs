using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using Earshot.Boot.Gate;
using Earshot.Contracts;

namespace Earshot.AudioProtection.Gate;

// One device change at a time across the Earshot processes that take this lock. \Earshot\Gate,
// \Earshot\Protect and \Earshot\BootBlock are separate tasks, and the scheduler's Queue policy only orders
// runs of the same task. Nothing documents BluetoothSetServiceState while the device nodes are being
// disabled, so a service change must never overlap a node change.
//
// Who takes it today: gate protect-on and protect-off, and the uninstall protection restore, so two protect
// verbs, or a protect verb and the restore, never run side by side. The block, allow, boot and set-device
// verbs and uninstall's node allow do not take it yet (they belong to the boot block feature); until they
// do, this lock does not keep a node change away from a service change. TryAcquireForNodeChange is the call
// they are meant to make.
//
// The lock is an open of %ProgramData%\Earshot\device-change.lock that asks for write access and shares only
// read, so a second writer's open fails with a sharing violation until the first handle closes. The gate has
// already checked that folder's ACL (only SYSTEM and administrators may create or replace entries), and the
// file is never read or written, so nothing is trusted from it. The file carries its own protected access
// list (DeviceChangeLockAccess), checked and if need be reset on every open, so a standard user cannot open
// it at all and so cannot hold a handle that keeps the gate out. Windows closes the handle when the process
// ends, however it ends, and the scheduler stops a task at its time limit, so a stuck holder cannot keep the
// lock past its task's limit.
//
// It is re-entrant on the thread that holds it, so a caller that already holds it (the whole gate run, say)
// can call code that takes it again. Dispose on the thread that acquired it.
// https://learn.microsoft.com/en-us/windows/win32/fileio/creating-and-opening-files
// https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-itasksettings-get_executiontimelimit
internal sealed class DeviceChangeLock : IDisposable
{
    public const string FileName = "device-change.lock";
    public const string StepName = "device-change-lock";
    public const string AccessStepName = "device-change-lock-access";

    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    // How long a block, allow, boot block or set-device waits for the lock: well inside \Earshot\Gate's PT2M
    // limit, so the node change itself still has time. A protect verb may hold the lock for up to
    // \Earshot\Protect's PT5M, so a node change can give up first; it then reports the busy lock as a failed
    // step and changes nothing, and the caller tries again.
    public static readonly TimeSpan NodeChangeLockTimeout = TimeSpan.FromSeconds(60);

    // ERROR_SHARING_VIOLATION and ERROR_LOCK_VIOLATION, and the HRESULT form FileStream reports them in.
    private const uint ErrorSharingViolation = 32;
    private const uint ErrorLockViolation = 33;
    private const int HResultSharingViolation = unchecked((int)0x80070020);
    private const int HResultLockViolation = unchecked((int)0x80070021);

    // Write access for the sharing check, and READ_CONTROL and WRITE_DAC so the access list can be checked
    // and reset through the same handle.
    private const FileSystemRights LockRights =
        FileSystemRights.WriteData | FileSystemRights.ReadPermissions | FileSystemRights.ChangePermissions | FileSystemRights.Synchronize;

    [ThreadStatic]
    private static Dictionary<string, DeviceChangeLock>? t_held;

    private readonly string _path;
    private FileStream? _stream;
    private int _depth = 1;

    private DeviceChangeLock(string path, FileStream stream)
    {
        _path = path;
        _stream = stream;
    }

    // Sleeps for the poll interval and keeps waiting.
    public static bool SleepAndContinue(TimeSpan delay)
    {
        Thread.Sleep(delay);
        return true;
    }

    // What a block, allow, boot block or set-device verb, or uninstall's node allow, calls before it changes a
    // node: the lock for this node API's machine, waiting NodeChangeLockTimeout. Null (with a failed step)
    // means change nothing.
    public static DeviceChangeLock? TryAcquireForNodeChange(string folder, INodeApi nodes, IList<StepOutcome> steps) =>
        TryAcquire(folder, DeviceChangeLockAccess.For(nodes), NodeChangeLockTimeout, SleepAndContinue, steps);

    // The lock, or null when it could not be taken within the timeout (another change still holds it), when
    // wait returned false, when the open failed for another reason, or when the file's access list could
    // not be made the expected one. Every outcome is a step.
    public static DeviceChangeLock? TryAcquire(
        string folder, DeviceChangeLockAccess access, TimeSpan timeout, Func<TimeSpan, bool> wait, IList<StepOutcome> steps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(wait);
        ArgumentNullException.ThrowIfNull(steps);
        string path = Path.GetFullPath(Path.Combine(folder, FileName));
        t_held ??= new Dictionary<string, DeviceChangeLock>(StringComparer.OrdinalIgnoreCase);
        if (t_held.TryGetValue(path, out DeviceChangeLock? held))
        {
            held._depth++;
            return held;
        }

        long retries = timeout <= TimeSpan.Zero ? 0 : (long)Math.Ceiling(timeout / PollInterval);
        for (long waited = 0; ; waited++)
        {
            FileStream stream;
            try
            {
                // The access list applies only when the file is created; an existing file is checked below.
                stream = FileSystemAclExtensions.Create(
                    new FileInfo(path), FileMode.OpenOrCreate, LockRights, FileShare.Read, bufferSize: 1, FileOptions.None, access.CreateSecurity());
            }
            catch (IOException ex) when (IsHeldElsewhere(ex))
            {
                if (waited >= retries)
                {
                    steps.Add(StepOutcomes.FromWin32(StepName, Win32Of(ex),
                        path + ": another Earshot device change was still running after " + Seconds(waited) + ", so nothing was changed."));
                    return null;
                }

                if (!wait(PollInterval))
                {
                    steps.Add(StepOutcomes.FromWin32(StepName, Win32Of(ex),
                        path + ": stopped waiting for another Earshot device change, so nothing was changed."));
                    return null;
                }

                continue;
            }
            catch (IOException ex)
            {
                steps.Add(StepOutcomes.FromHResult(StepName, ex.HResult, path + ": " + ex.Message));
                return null;
            }
            catch (UnauthorizedAccessException ex)
            {
                steps.Add(StepOutcomes.FromHResult(StepName, ex.HResult, path + ": " + ex.Message));
                return null;
            }

            StepOutcome? secured = Secure(stream, path, access);
            if (secured is { Ok: false })
            {
                stream.Dispose();
                steps.Add(secured);
                return null;
            }

            var acquired = new DeviceChangeLock(path, stream);
            t_held[path] = acquired;
            steps.Add(StepOutcomes.FromHResult(StepName, 0,
                waited == 0 ? path : path + ", after waiting " + Seconds(waited) + " for another device change."));
            if (secured is not null)
            {
                steps.Add(secured);
            }

            return acquired;
        }
    }

    public void Dispose()
    {
        if (_stream is null || --_depth > 0)
        {
            return;
        }

        t_held?.Remove(_path);
        _stream.Dispose();
        _stream = null;
    }

    // True for the step of a lock that another device change still held, as opposed to an open that failed.
    public static bool IsBusy(StepOutcome step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step.Step == StepName && !step.Ok && step.Code is (int)ErrorSharingViolation or (int)ErrorLockViolation;
    }

    // Null when the open file already has the expected access list; otherwise a step saying it was reset
    // (Ok) or could not be made right (failed, so the caller changes nothing).
    // https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemaclextensions.getaccesscontrol
    private static StepOutcome? Secure(FileStream stream, string path, DeviceChangeLockAccess access)
    {
        try
        {
            string? problem = access.Problem(stream.GetAccessControl());
            if (problem is null)
            {
                return null;
            }

            stream.SetAccessControl(access.CreateSecurity());
            string? still = access.Problem(stream.GetAccessControl());
            return still is null
                ? StepOutcomes.FromHResult(AccessStepName, 0, path + ": " + problem + " Its access list was reset to " + access.Sddl + ".")
                : StepOutcomes.NotAttempted(AccessStepName, path + ": " + still + " It stayed so after a reset, so nothing was changed.");
        }
        catch (UnauthorizedAccessException ex)
        {
            return StepOutcomes.FromHResult(AccessStepName, ex.HResult, path + ": the access list could not be checked or reset, so nothing was changed. " + ex.Message);
        }
        catch (IOException ex)
        {
            return StepOutcomes.FromHResult(AccessStepName, ex.HResult, path + ": the access list could not be checked or reset, so nothing was changed. " + ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return StepOutcomes.FromHResult(AccessStepName, ex.HResult, path + ": the access list could not be checked or reset, so nothing was changed. " + ex.Message);
        }
    }

    private static bool IsHeldElsewhere(IOException ex) =>
        ex.HResult is HResultSharingViolation or HResultLockViolation;

    // The CreateFile error behind the HRESULT, so the step is named from the Win32 table.
    private static uint Win32Of(IOException ex) =>
        ex.HResult == HResultLockViolation ? ErrorLockViolation : ErrorSharingViolation;

    private static string Seconds(long polls) =>
        (polls * PollInterval).TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture) + " s";
}

// Who may open device-change.lock: a protected access list granting SYSTEM and Administrators full control
// and OWNER RIGHTS read control only. Nobody else can open the file, so no standard user can hold a read
// handle that makes the gate's write open fail. The OWNER RIGHTS entry takes away the implicit WRITE_DAC an
// owner outside those two would otherwise have, so such an owner cannot open the list up again.
//
// The real node API (the SYSTEM gate and the elevated uninstall) gets Machine. Any other node API is a test's
// fake node table, whose gate runs unelevated as the current user, so that user is granted full control too.
// https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/manage/understand-security-identifiers (Owner Rights, S-1-3-4)
// https://learn.microsoft.com/en-us/windows/win32/fileio/file-security-and-access-rights
// https://learn.microsoft.com/en-us/windows/win32/secauthz/ace-strings
internal sealed class DeviceChangeLockAccess
{
    public const string OwnerRightsSid = "S-1-3-4";

    private const int FileAllAccess = 0x001F01FF;
    private const int ReadControl = 0x00020000;

    private readonly Dictionary<SecurityIdentifier, int> _aces = new();

    private DeviceChangeLockAccess(IEnumerable<SecurityIdentifier> holders)
    {
        foreach (SecurityIdentifier holder in holders)
        {
            _aces[holder] = FileAllAccess;
        }

        _aces[new SecurityIdentifier(OwnerRightsSid)] = ReadControl;
        Sddl = "D:P" + string.Concat(_aces.Select(a =>
            "(A;;0x" + a.Value.ToString("x", CultureInfo.InvariantCulture) + ";;;" + a.Key.Value + ")"));
    }

    public static DeviceChangeLockAccess Machine { get; } =
        new([new SecurityIdentifier(Boot.Sddl.LocalSystemSid), new SecurityIdentifier(Boot.Sddl.AdministratorsSid)]);

    // The access list as SDDL, with SIDs in their S-1-... form.
    public string Sddl { get; }

    public IReadOnlyCollection<SecurityIdentifier> Holders => _aces.Where(a => a.Value == FileAllAccess).Select(a => a.Key).ToList();

    public static DeviceChangeLockAccess For(INodeApi nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        return nodes is CfgMgr32NodeApi ? Machine : WithCurrentUser();
    }

    // Machine plus the user this process runs as.
    internal static DeviceChangeLockAccess WithCurrentUser()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User ?? throw new InvalidOperationException("The current process has no user SID.");
        return new DeviceChangeLockAccess([new SecurityIdentifier(Boot.Sddl.LocalSystemSid), new SecurityIdentifier(Boot.Sddl.AdministratorsSid), user]);
    }

    public FileSecurity CreateSecurity()
    {
        var security = new FileSecurity();
        security.SetSecurityDescriptorSddlForm(Sddl, AccessControlSections.Access);
        return security;
    }

    // Null when the access list is exactly this one: protected, and one explicit allow entry per SID with
    // exactly its mask. Deny entries, inherited entries and any other SID count as problems, and so does a
    // missing entry, because the fix is always the same reset.
    public string? Problem(FileSecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);
        var sd = new RawSecurityDescriptor(security.GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        if (sd.DiscretionaryAcl is null)
        {
            return "It has no access list, which lets everyone open it.";
        }

        if ((sd.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0)
        {
            return "Its access list is not protected, so it takes entries from the folder.";
        }

        var seen = new HashSet<SecurityIdentifier>();
        foreach (GenericAce ace in sd.DiscretionaryAcl)
        {
            if (ace is not CommonAce { AceQualifier: AceQualifier.AccessAllowed } allowed || ace.AceFlags != AceFlags.None ||
                !_aces.TryGetValue(allowed.SecurityIdentifier, out int mask) || allowed.AccessMask != mask ||
                !seen.Add(allowed.SecurityIdentifier))
            {
                return "Its access list has an entry other than " + Sddl + ".";
            }
        }

        return seen.Count == _aces.Count ? null : "Its access list is missing an entry of " + Sddl + ".";
    }
}
