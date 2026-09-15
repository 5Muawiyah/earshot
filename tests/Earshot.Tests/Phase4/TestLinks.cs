using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Earshot.Tests.Phase4;

// Makes the links a user can make without any privilege, inside a test's own temp folder: a directory
// junction (a mount point reparse point) and a hard link. A symbolic link needs a privilege most accounts
// lack, so the guards that must hold against links are tested with these.
// https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_set_reparse_point
// https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_reparse_data_buffer
// https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createhardlinkw
internal static class TestLinks
{
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ_WRITE_DELETE = 0x00000007;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
    private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;

    // Creates an empty folder at link and turns it into a junction to target. The buffer is the
    // REPARSE_DATA_BUFFER header (tag, data length, reserved) followed by MountPointReparseBuffer: the
    // substitute name (\??\ plus the full path) and the print name, each NUL-terminated.
    //
    // Dispose the result before the temp folder: a recursive Directory.Delete meeting a junction first
    // tries DeleteVolumeMountPoint, which needs an administrator, so the junction is removed on its own
    // with RemoveDirectoryW (Directory.Delete without recursion), which removes the link and not its target.
    // https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-removedirectoryw
    public static IDisposable CreateJunction(string link, string target)
    {
        MakeJunction(link, target);
        return new JunctionRemover(link);
    }

    private sealed class JunctionRemover(string link) : IDisposable
    {
        public void Dispose()
        {
            var info = new DirectoryInfo(link);
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                info.Delete(recursive: false);
            }
        }
    }

    private static void MakeJunction(string link, string target)
    {
        string full = Path.GetFullPath(target);
        byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + full);
        byte[] print = Encoding.Unicode.GetBytes(full);
        int dataLength = 8 + substitute.Length + 2 + print.Length + 2;
        byte[] buffer = new byte[8 + dataLength];
        Span<byte> span = buffer;
        BitConverter.TryWriteBytes(span[0..], IO_REPARSE_TAG_MOUNT_POINT);
        BitConverter.TryWriteBytes(span[4..], checked((ushort)dataLength));
        BitConverter.TryWriteBytes(span[8..], (ushort)0);
        BitConverter.TryWriteBytes(span[10..], checked((ushort)substitute.Length));
        BitConverter.TryWriteBytes(span[12..], checked((ushort)(substitute.Length + 2)));
        BitConverter.TryWriteBytes(span[14..], checked((ushort)print.Length));
        substitute.CopyTo(buffer, 16);
        print.CopyTo(buffer, 16 + substitute.Length + 2);

        Directory.CreateDirectory(link);
        using SafeFileHandle handle = CreateFileW(link, GENERIC_WRITE, FILE_SHARE_READ_WRITE_DELETE, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateFileW " + link);
        }

        if (!DeviceIoControl(handle, FSCTL_SET_REPARSE_POINT, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "FSCTL_SET_REPARSE_POINT " + link);
        }
    }

    public static void CreateHardLink(string link, string existing)
    {
        if (!CreateHardLinkW(link, existing, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateHardLinkW " + link);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode, byte[] lpInBuffer, int nInBufferSize, IntPtr lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
