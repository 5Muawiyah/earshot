using System.Runtime.InteropServices;
using Earshot.Contracts;
using Earshot.Interop;
using Microsoft.Win32.SafeHandles;

namespace Earshot.Boot.Gate;

// Holds a folder open, and says whether it is the folder that was meant, while a file is written into it. The machine folder
// is checked (owner, access list, not a link) by the gate before it trusts anything in it, but the check and the write are two
// moments, and in between the folder can be removed (uninstall does exactly that) and a standard user can put a directory
// junction of the same name in its place: a SYSTEM write would then land wherever the junction points. A pin closes the gap.
// The folder is opened without delete sharing, so while the pin is held the folder cannot be renamed, deleted or replaced,
// and what the pin found is what the write goes into.
internal interface IFolderPinner
{
    // The pin, to be disposed after the write, or null with the reason in step: the folder cannot be opened, is a link, is not
    // where the path leads, or fails its check.
    IDisposable? Pin(string path, out StepOutcome step);
}

// https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew
// https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfileinformationbyhandle
// https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfinalpathnamebyhandlew
internal sealed unsafe class WindowsFolderPinner : IFolderPinner
{
    public const string StepName = "status-folder-pin";

    private readonly IFolderSecurity _folders;
    private readonly Func<string?, IReadOnlyList<string>> _check;

    // check is the rule the folder has to pass once it is held, applied to its owner and access list as SDDL.
    public WindowsFolderPinner(IFolderSecurity folders, Func<string?, IReadOnlyList<string>> check)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(check);
        _folders = folders;
        _check = check;
    }

    public IDisposable? Pin(string path, out StepOutcome step)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // The reparse point itself is opened, never what it points to, so a junction shows up as one. Sharing read and write
        // but not delete is what keeps the folder where it is.
        SafeFileHandle handle = FileApis.CreateFile(
            path, FileApis.FILE_LIST_DIRECTORY | FileApis.FILE_READ_ATTRIBUTES, FileApis.FILE_SHARE_READ | FileApis.FILE_SHARE_WRITE, 0,
            FileApis.OPEN_EXISTING, FileApis.FILE_FLAG_BACKUP_SEMANTICS | FileApis.FILE_FLAG_OPEN_REPARSE_POINT, 0);
        if (handle.IsInvalid)
        {
            uint error = unchecked((uint)Marshal.GetLastPInvokeError());
            handle.Dispose();
            step = StepOutcomes.FromWin32(StepName, error, "The folder could not be opened to write into it: " + path);
            return null;
        }

        try
        {
            if (!FileApis.GetFileInformationByHandle(handle, out BY_HANDLE_FILE_INFORMATION info))
            {
                uint error = unchecked((uint)Marshal.GetLastPInvokeError());
                step = StepOutcomes.FromWin32(StepName, error, "The folder could not be read once open: " + path);
                handle.Dispose();
                return null;
            }

            if ((info.FileAttributes & FileApis.FILE_ATTRIBUTE_REPARSE_POINT) != 0 || (info.FileAttributes & FileApis.FILE_ATTRIBUTE_DIRECTORY) == 0)
            {
                step = StepOutcomes.NotAttempted(StepName, path + " is a link or not a folder, so nothing was written into it.");
                handle.Dispose();
                return null;
            }

            // A link higher up the path leads somewhere else without the last part being one.
            string? final = FinalPath(handle);
            if (final is null || !string.Equals(final, Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), StringComparison.OrdinalIgnoreCase))
            {
                step = StepOutcomes.NotAttempted(StepName,
                    path + " leads to " + (final ?? "a place that could not be read") + ", so nothing was written into it.");
                handle.Dispose();
                return null;
            }

            // Held now, so the owner and access list read next are the ones of the folder the write goes into.
            StepOutcome read = _folders.ReadSddl(path, out string? sddl);
            if (!read.Ok)
            {
                step = read;
                handle.Dispose();
                return null;
            }

            IReadOnlyList<string> problems = _check(sddl);
            if (problems.Count != 0)
            {
                step = StepOutcomes.NotAttempted(StepName, path + " failed its check: " + string.Join("; ", problems));
                handle.Dispose();
                return null;
            }

            step = new StepOutcome(StepName, true, 0, "S_OK", path);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    // The path the open handle really is at, without the \\?\ prefix. Null when the call fails.
    private static string? FinalPath(SafeFileHandle handle)
    {
        char* buffer = stackalloc char[1024];
        uint length = FileApis.GetFinalPathNameByHandle(handle, buffer, 1024, FileApis.FILE_NAME_NORMALIZED_VOLUME_NAME_DOS);
        if (length == 0 || length >= 1024)
        {
            return null;
        }

        string text = new(buffer, 0, (int)length);
        return Path.TrimEndingDirectorySeparator(text.StartsWith(@"\\?\", StringComparison.Ordinal) ? text[4..] : text);
    }
}
