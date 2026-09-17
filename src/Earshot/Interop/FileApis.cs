using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Earshot.Interop;

// The kernel32 file calls the elevated install and uninstall make: opening a file or folder for a check that a
// path is what it claims to be (CreateFileW, GetFileInformationByHandle, GetFinalPathNameByHandleW), and
// scheduling a delete for the next restart (MoveFileExW). Declared here, with the rest of the native surface,
// rather than next to their callers.
internal static unsafe partial class FileApis
{
    private const string Kernel32 = "kernel32.dll";

    // CreateFileW access, share, disposition and flag values (fileapi.h, winnt.h).
    // https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew
    internal const uint FILE_LIST_DIRECTORY = 0x00000001;
    internal const uint FILE_READ_ATTRIBUTES = 0x00000080;
    internal const uint FILE_SHARE_READ = 0x00000001;
    internal const uint FILE_SHARE_WRITE = 0x00000002;
    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    internal const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    // File attribute bits in BY_HANDLE_FILE_INFORMATION.dwFileAttributes.
    // https://learn.microsoft.com/en-us/windows/win32/fileio/file-attribute-constants
    internal const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    internal const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;

    // GetFinalPathNameByHandleW flags: FILE_NAME_NORMALIZED | VOLUME_NAME_DOS.
    // https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfinalpathnamebyhandlew
    internal const uint FILE_NAME_NORMALIZED_VOLUME_NAME_DOS = 0;

    // MoveFileExW: delete (a NULL new name) at the next restart. Needs an administrator or Local System.
    // https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw
    internal const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;

    // https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew
    [LibraryImport(Kernel32, EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, nint lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, nint hTemplateFile);

    // https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfileinformationbyhandle
    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    // Returns the length without the terminator when the buffer is big enough, the size needed (with the
    // terminator) when it is not, and 0 on failure.
    // https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfinalpathnamebyhandlew
    [LibraryImport(Kernel32, EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    internal static partial uint GetFinalPathNameByHandle(SafeFileHandle hFile, char* lpszFilePath, uint cchFilePath, uint dwFlags);

    // https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw
    [LibraryImport(Kernel32, EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, uint dwFlags);
}

// BY_HANDLE_FILE_INFORMATION, 52 bytes.
// https://learn.microsoft.com/en-us/windows/win32/api/fileapi/ns-fileapi-by_handle_file_information
[StructLayout(LayoutKind.Sequential)]
internal struct BY_HANDLE_FILE_INFORMATION
{
    public uint FileAttributes;
    public uint CreationTimeLow;
    public uint CreationTimeHigh;
    public uint LastAccessTimeLow;
    public uint LastAccessTimeHigh;
    public uint LastWriteTimeLow;
    public uint LastWriteTimeHigh;
    public uint VolumeSerialNumber;
    public uint FileSizeHigh;
    public uint FileSizeLow;
    public uint NumberOfLinks;
    public uint FileIndexHigh;
    public uint FileIndexLow;
}
