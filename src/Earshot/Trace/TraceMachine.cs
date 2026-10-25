using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using Earshot.Boot.Gate;
using Earshot.Infra;

namespace Earshot.Tracing;

// The real machine for the helper. The trace folder sits under the machine folder (%ProgramData%\Earshot), whose descriptor
// lets everyone read; each run gets a folder of its own that only SYSTEM and administrators can open, because a trace holds
// Bluetooth addresses and may hold pairing keys. The tray's user is then given read on the two files they need, by SID.
internal sealed class TraceMachine : ITraceMachine
{
    // Protected (no inheritance from the parent), owner and group Administrators / System, full control for SYSTEM and
    // administrators only. Built from the SDDL grammar:
    // https://learn.microsoft.com/en-us/windows/win32/secauthz/security-descriptor-string-format
    internal const string RunFolderSddl = "O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";

    private readonly IFolderSecurity _folders;
    private readonly string _machineFolder;

    public TraceMachine(Paths paths, IFolderSecurity folders)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(folders);
        _folders = folders;
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
        foreach (string parent in new[] { _machineFolder, TraceFolder })
        {
            var made = _folders.CreateHardened(parent);
            if (!made.Ok)
            {
                return made.Step + " " + made.CodeName + ": " + made.Detail;
            }
        }

        var run = _folders.CreateWithSddl(runFolder, RunFolderSddl);
        return run.Ok ? null : run.Step + " " + run.CodeName + ": " + run.Detail;
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
            return path + ": " + ex.Message;
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
            System.Diagnostics.Trace.WriteLine("Earshot trace: " + path + " could not be read: " + ex.Message);
            return null;
        }
    }

    public void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or SecurityException)
        {
            System.Diagnostics.Trace.WriteLine("Earshot trace: " + path + " could not be deleted: " + ex.Message);
        }
    }

    public void DeleteRunFolder(string runFolder)
    {
        // Only a folder this helper made under the trace folder, never anything a caller names.
        string full = Path.GetFullPath(runFolder);
        string parent = Path.GetFullPath(TraceFolder).TrimEnd('\\') + "\\";
        if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || full.Length <= parent.Length)
        {
            System.Diagnostics.Trace.WriteLine("Earshot trace: refused to delete " + runFolder + ", which is not inside " + TraceFolder + ".");
            return;
        }

        try
        {
            if ((new DirectoryInfo(full).Attributes & FileAttributes.ReparsePoint) != 0)
            {
                System.Diagnostics.Trace.WriteLine("Earshot trace: " + full + " is a reparse point and was not deleted.");
                return;
            }

            Directory.Delete(full, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or SecurityException)
        {
            System.Diagnostics.Trace.WriteLine("Earshot trace: " + full + " could not be deleted: " + ex.Message);
        }
    }

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
            return path + ": " + ex.Message;
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
