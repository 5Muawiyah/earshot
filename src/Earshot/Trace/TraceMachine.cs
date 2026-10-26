using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Infra;

namespace Earshot.Tracing;

// The real machine for the helper. The trace folder sits under the machine folder (%ProgramData%\Earshot), whose descriptor
// lets everyone read; each run gets a folder of its own that only SYSTEM and administrators can open, because a trace holds
// Bluetooth addresses and may hold pairing keys. The tray's user is then given read on the two files they need, by SID.
//
// A standard user can make %ProgramData%\Earshot or its trace folder before Earshot does, and making a folder that is already
// there changes nothing about it. So both are read back and must pass the same check the machine log's folder passes
// (AclCheck.CheckMachineFolder: owner Administrators or SYSTEM, protected, nobody else may write), and no folder on the way,
// from the drive down to the run folder, may be a reparse point. Once both pass, only an administrator can change what is
// inside them, so the profile that was checked is the one wpr.exe opens and the files are saved where they were meant to be.
// Every failure goes to the helper's log, which the trace mode writes to the machine log when it ends.
internal sealed class TraceMachine : ITraceMachine
{
    // Protected (no inheritance from the parent), owner and group Administrators / System, full control for SYSTEM and
    // administrators only. Built from the SDDL grammar:
    // https://learn.microsoft.com/en-us/windows/win32/secauthz/security-descriptor-string-format
    internal const string RunFolderSddl = "O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";

    private readonly IFolderSecurity _folders;
    private readonly ILog _log;
    private readonly string _machineFolder;

    public TraceMachine(Paths paths, IFolderSecurity folders, ILog log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(log);
        _folders = folders;
        _log = log;
        _machineFolder = paths.MachineFolder;
        TraceFolder = Path.Combine(paths.MachineFolder, "trace");
        WprPath = WprCommand.ExePath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    }

    public string WprPath { get; }

    public string TraceFolder { get; }

    public bool FileExists(string path) => File.Exists(path);

    public string? PrepareRunFolder(string nonce, out string runFolder)
    {
        runFolder = Path.Combine(TraceFolder, nonce);
        string? linked = FirstReparsePoint(_machineFolder);
        if (linked is not null)
        {
            return Refuse("trace-folder-path", linked + " is a reparse point, so nothing is made below it.");
        }

        foreach (string parent in new[] { _machineFolder, TraceFolder })
        {
            StepOutcome made = _folders.CreateHardened(parent);
            if (!made.Ok)
            {
                return Refuse(made);
            }

            // ReadSddl refuses a reparse point, so a folder swapped for a junction after it was made fails here.
            StepOutcome read = _folders.ReadSddl(parent, out string? sddl);
            if (!read.Ok)
            {
                return Refuse(read);
            }

            IReadOnlyList<string> problems = AclCheck.CheckMachineFolder(sddl);
            if (problems.Count > 0)
            {
                return Refuse("trace-folder-acl", parent + " can be changed by more than administrators, so no trace is recorded there: " + string.Join(" ", problems));
            }
        }

        StepOutcome run = _folders.CreateWithSddl(runFolder, RunFolderSddl);
        if (!run.Ok)
        {
            return Refuse(run);
        }

        StepOutcome runRead = _folders.ReadSddl(runFolder, out _);
        return runRead.Ok ? null : Refuse(runRead);
    }

    // The first folder from the drive's root down to path (path included) that is a reparse point, or null. A folder that is
    // not there yet is not a link, and the ones below it are made by this helper.
    internal static string? FirstReparsePoint(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var chain = new List<string>();
        for (string? at = Path.GetFullPath(path); !string.IsNullOrEmpty(at); at = Path.GetDirectoryName(at))
        {
            chain.Add(at);
        }

        chain.Reverse();
        foreach (string folder in chain)
        {
            var info = new DirectoryInfo(folder);
            if (info.Exists && info.Parent is not null && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return folder;
            }
        }

        return null;
    }

    private string Refuse(StepOutcome step) => Refuse(step.Step, step.CodeName + ": " + step.Detail);

    private string Refuse(string step, string detail)
    {
        string reason = step + " " + detail;
        _log.Warn("trace: refused to use the trace folder: " + reason);
        return reason;
    }

    public string? WriteFile(string path, byte[] bytes)
    {
        try
        {
            File.WriteAllBytes(path, bytes);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or SecurityException)
        {
            return path + ": " + Describe(ex);
        }
    }

    public byte[]? ReadFile(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or SecurityException)
        {
            _log.Warn("trace: " + path + " could not be read: " + Describe(ex));
            return null;
        }
    }

    public string? DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or SecurityException)
        {
            string problem = path + " could not be deleted: " + Describe(ex);
            _log.Warn("trace: " + problem);
            return problem;
        }
    }

    public string? DeleteRunFolder(string runFolder)
    {
        // Only a folder this helper made under the trace folder, never anything a caller names.
        string full = Path.GetFullPath(runFolder);
        string parent = Path.GetFullPath(TraceFolder).TrimEnd('\\') + "\\";
        if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || full.Length <= parent.Length)
        {
            string refused = "refused to delete " + runFolder + ", which is not inside " + TraceFolder + ".";
            _log.Warn("trace: " + refused);
            return refused;
        }

        try
        {
            if ((new DirectoryInfo(full).Attributes & FileAttributes.ReparsePoint) != 0)
            {
                string linked = full + " is a reparse point and was not deleted.";
                _log.Warn("trace: " + linked);
                return linked;
            }

            Directory.Delete(full, recursive: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or SecurityException)
        {
            string problem = full + " could not be deleted: " + Describe(ex);
            _log.Warn("trace: " + problem);
            return problem;
        }
    }

    // The type and the raw HRESULT with the message, so a failure in the log can be told apart from another.
    private static string Describe(Exception ex) =>
        ex.GetType().Name + " (HRESULT 0x" + ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + "): " + ex.Message;

    public string? GrantRead(string path, int trayPid)
    {
        SecurityIdentifier? sid = TrayUser.SidOf(trayPid, out string? problem);
        if (sid is null)
        {
            return problem;
        }

        try
        {
            var info = new FileInfo(path);
            FileSecurity security = info.GetAccessControl(AccessControlSections.Access);
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Read, AccessControlType.Allow));
            info.SetAccessControl(security);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ArgumentException or SecurityException)
        {
            return path + ": " + Describe(ex);
        }
    }
}

// The account the tray runs as, read from its process token. The helper is elevated and may run as a different account's
// token than the tray, so the helper's own identity is not used.
// https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-openprocess
// https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-openprocesstoken
internal static partial class TrayUser
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const uint TokenDuplicate = 0x0002;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    public static SecurityIdentifier? SidOf(int processId, out string? problem)
    {
        problem = null;
        nint process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (process == 0)
        {
            problem = "OpenProcess for process " + processId + " failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return null;
        }

        try
        {
            if (!OpenProcessToken(process, TokenQuery | TokenDuplicate, out nint token))
            {
                problem = "OpenProcessToken for process " + processId + " failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return null;
            }

            try
            {
                using var identity = new WindowsIdentity(token);
                if (identity.User is null)
                {
                    problem = "The token of process " + processId + " has no user.";
                    return null;
                }

                return identity.User;
            }
            finally
            {
                _ = CloseHandle(token);
            }
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }
}
