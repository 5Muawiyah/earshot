using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Earshot.Contracts;
using Microsoft.Win32.SafeHandles;

namespace Earshot.Boot.Gate;

internal sealed record CopiedFile(string RelativePath, long Length, string Sha256);

internal sealed record IntegrityCopyResult(bool Ok, IReadOnlyList<CopiedFile> Files, IReadOnlyList<StepOutcome> Steps);

// Copies the published files of the application folder for install and proves the copy is what was read.
//
// What is copied is the list install read from the publish manifest (Earshot.files.json), never whatever the
// folder happens to hold: a release unzipped into a busy folder (Downloads, say) must not put unrelated files
// into %ProgramFiles%\Earshot, which is the elevated gate's DLL search folder.
//
// The unzip folder is user-writable, so a process running as the user could swap a file while the elevated
// install copies it. Every source file is opened first and held open with FileShare.Read for the whole
// copy, which denies any other writer, rename or delete (no FileShare.Write, no FileShare.Delete). Each file
// is hashed with SHA-256 from its open handle, copied from the same handle while the bytes are hashed
// again, and the destination is re-read and hashed a third time. Any difference, a reparse point anywhere
// in the source, or any I/O failure fails the whole copy; the caller then removes the destination.
//
// Listing the source and opening each file are separate steps, and an open by path follows links, so a
// subfolder swapped for a junction in between would make the elevated copy read a file from somewhere else.
// The source folder itself is held open (without FILE_SHARE_DELETE, so it cannot be renamed away) and must
// not be a reparse point, and after each file is opened its handle's final path must be exactly the source
// folder's final path plus the listed relative path, with one link. Anything else fails the copy before a
// byte is read.
// https://learn.microsoft.com/en-us/dotnet/api/system.io.fileshare
// https://learn.microsoft.com/en-us/windows/win32/fileio/creating-and-opening-files
// https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfinalpathnamebyhandlew
// https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.sha256
internal static unsafe partial class IntegrityCopy
{
    public const int MaxFiles = 5000;
    private const int BufferSize = 81920;

    // CreateFileW and GetFileInformationByHandle values (fileapi.h, winnt.h).
    private const uint FILE_LIST_DIRECTORY = 0x00000001;
    private const uint FILE_READ_ATTRIBUTES = 0x00000080;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;

    // GetFinalPathNameByHandleW flags: FILE_NAME_NORMALIZED | VOLUME_NAME_DOS.
    private const uint FinalPathFlags = 0;

    // files: the relative paths to copy, in the order they were listed. Every one must be there.
    public static IntegrityCopyResult Copy(string sourceFolder, string destinationFolder, IReadOnlyList<string> files) =>
        Copy(sourceFolder, destinationFolder, files, afterListing: null);

    // afterListing runs after the source list is settled and before its files are opened. Tests use it to
    // change the source in that window.
    internal static IntegrityCopyResult Copy(string sourceFolder, string destinationFolder, IReadOnlyList<string> files, Action? afterListing)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFolder);
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceFolder));
        string destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationFolder));
        var steps = new List<StepOutcome>();
        var copied = new List<CopiedFile>();
        var opened = new List<(string Relative, FileStream Stream)>();
        SafeFileHandle? root = null;

        try
        {
            if (IsInside(destination, source) || IsInside(source, destination))
            {
                steps.Add(StepOutcomes.NotAttempted("copy-app", "The source and destination folders overlap."));
                return new IntegrityCopyResult(false, copied, steps);
            }

            if (!TryHoldSourceFolder(source, steps, out root, out string? rootFinal))
            {
                return new IntegrityCopyResult(false, copied, steps);
            }

            if (files.Count == 0 || files.Count > MaxFiles)
            {
                steps.Add(StepOutcomes.NotAttempted("copy-app", "The file list is empty or holds more than " + MaxFiles + " files."));
                return new IntegrityCopyResult(false, copied, steps);
            }

            List<string> relativeFiles = files.ToList();

            afterListing?.Invoke();

            // Hold every source file before reading any of them, and check where each handle really is.
            foreach (string relative in relativeFiles)
            {
                string path = Path.Combine(source, relative);
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
                opened.Add((relative, stream));
                StepOutcome? problem = CheckOpenedFile(stream.SafeFileHandle, rootFinal, relative);
                if (problem is not null)
                {
                    steps.Add(problem);
                    return new IntegrityCopyResult(false, copied, steps);
                }
            }

            Directory.CreateDirectory(destination);
            foreach ((string relative, FileStream stream) in opened)
            {
                string target = Path.GetFullPath(Path.Combine(destination, relative));
                if (!IsInside(target, destination))
                {
                    steps.Add(StepOutcomes.NotAttempted("copy-app:" + relative, "The file would land outside the destination."));
                    return new IntegrityCopyResult(false, copied, steps);
                }

                string? folder = Path.GetDirectoryName(target);
                if (folder is not null)
                {
                    Directory.CreateDirectory(folder);
                }

                stream.Position = 0;
                byte[] before = SHA256.HashData(stream);
                stream.Position = 0;
                byte[] during;
                long length;
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.None))
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    byte[] buffer = new byte[BufferSize];
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        hash.AppendData(buffer, 0, read);
                        output.Write(buffer, 0, read);
                    }

                    output.Flush(flushToDisk: true);
                    length = output.Length;
                    during = hash.GetHashAndReset();
                }

                if (!CryptographicOperations.FixedTimeEquals(before, during) || length != stream.Length)
                {
                    steps.Add(StepOutcomes.NotAttempted("copy-app:" + relative, "The source changed while it was copied."));
                    return new IntegrityCopyResult(false, copied, steps);
                }

                copied.Add(new CopiedFile(relative, length, Convert.ToHexString(before)));
            }

            foreach (CopiedFile file in copied)
            {
                string target = Path.Combine(destination, file.RelativePath);
                byte[] after;
                long length;
                using (var check = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan))
                {
                    length = check.Length;
                    after = SHA256.HashData(check);
                }

                if (length != file.Length || !string.Equals(Convert.ToHexString(after), file.Sha256, StringComparison.Ordinal))
                {
                    steps.Add(StepOutcomes.NotAttempted("verify-copy:" + file.RelativePath, "The copied file does not match its source hash."));
                    return new IntegrityCopyResult(false, copied, steps);
                }
            }

            steps.Add(new StepOutcome("copy-app", true, 0, "S_OK",
                copied.Count.ToString(CultureInfo.InvariantCulture) + " published files copied and verified with SHA-256."));
            return new IntegrityCopyResult(true, copied, steps);
        }
        catch (IOException ex)
        {
            steps.Add(StepOutcomes.FromHResult("copy-app", ex.HResult, ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            steps.Add(StepOutcomes.FromHResult("copy-app", ex.HResult, ex.Message));
        }
        finally
        {
            foreach ((_, FileStream stream) in opened)
            {
                stream.Dispose();
            }

            root?.Dispose();
        }

        return new IntegrityCopyResult(false, copied, steps);
    }

    // Opens the source folder itself (not what it may point to) for listing and attributes with no
    // FILE_SHARE_DELETE, so it cannot be renamed or replaced while the copy runs, and reads its final path.
    // (An open for attributes alone would not take part in share checks.) A folder that is missing, is not a
    // directory or is a reparse point fails.
    // https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew
    // https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfileinformationbyhandle
    private static bool TryHoldSourceFolder(string source, List<StepOutcome> steps, out SafeFileHandle? handle, out string? finalPath)
    {
        finalPath = null;
        handle = CreateFile(source, FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, 0);
        if (handle.IsInvalid)
        {
            uint error = unchecked((uint)Marshal.GetLastPInvokeError());
            handle.Dispose();
            handle = null;
            steps.Add(StepOutcomes.FromWin32("copy-app", error, "The source folder could not be opened: " + source));
            return false;
        }

        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation info))
        {
            steps.Add(StepOutcomes.FromWin32("copy-app", unchecked((uint)Marshal.GetLastPInvokeError()), "The source folder could not be read: " + source));
            return false;
        }

        if ((info.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
        {
            steps.Add(StepOutcomes.NotAttempted("copy-app", "The source folder is a reparse point."));
            return false;
        }

        if ((info.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0)
        {
            steps.Add(StepOutcomes.NotAttempted("copy-app", "The source is not a folder: " + source));
            return false;
        }

        return TryGetFinalPath(handle, "copy-app", steps, out finalPath);
    }

    // Null when the opened file is the listed one: its final path is the source folder's final path plus the
    // relative path, it is not a folder, and it has exactly one link (a hard link could be a second name for
    // a file outside the source).
    private static StepOutcome? CheckOpenedFile(SafeFileHandle handle, string? rootFinal, string relative)
    {
        string step = "copy-app:" + relative;
        var steps = new List<StepOutcome>();
        if (rootFinal is null || !TryGetFinalPath(handle, step, steps, out string? final))
        {
            return steps.Count > 0 ? steps[0] : StepOutcomes.NotAttempted(step, "The source folder path is unknown.");
        }

        string expected = rootFinal + Path.DirectorySeparatorChar + relative;
        if (!string.Equals(final, expected, StringComparison.OrdinalIgnoreCase))
        {
            return StepOutcomes.NotAttempted(step, "The file opened is not the one listed; the source changed after it was listed: " + final);
        }

        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation info))
        {
            return StepOutcomes.FromWin32(step, unchecked((uint)Marshal.GetLastPInvokeError()), "The file could not be read.");
        }

        if ((info.FileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) != 0)
        {
            return StepOutcomes.NotAttempted(step, "The file opened is a folder or a reparse point.");
        }

        return info.NumberOfLinks == 1
            ? null
            : StepOutcomes.NotAttempted(step, "The file has " + info.NumberOfLinks.ToString(CultureInfo.InvariantCulture) + " hard links; only a file with one is copied.");
    }

    // GetFinalPathNameByHandleW returns the length without the terminator when the buffer is big enough, the
    // size needed (with the terminator) when it is not, and 0 on failure.
    private static bool TryGetFinalPath(SafeFileHandle handle, string step, List<StepOutcome> steps, out string? finalPath)
    {
        finalPath = null;
        uint capacity = 512;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            char[] buffer = new char[capacity];
            uint length;
            fixed (char* p = buffer)
            {
                length = GetFinalPathNameByHandle(handle, p, capacity, FinalPathFlags);
            }

            if (length == 0)
            {
                steps.Add(StepOutcomes.FromWin32(step, unchecked((uint)Marshal.GetLastPInvokeError()), "The final path could not be read."));
                return false;
            }

            if (length < capacity)
            {
                finalPath = new string(buffer, 0, (int)length);
                return true;
            }

            capacity = length;
        }

        steps.Add(StepOutcomes.NotAttempted(step, "The final path kept growing while it was read."));
        return false;
    }

    internal static bool IsInside(string path, string folder)
    {
        string p = Path.TrimEndingDirectorySeparator(path);
        string f = Path.TrimEndingDirectorySeparator(folder);
        return p.Equals(f, StringComparison.OrdinalIgnoreCase) ||
               p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // BY_HANDLE_FILE_INFORMATION, 52 bytes.
    // https://learn.microsoft.com/en-us/windows/win32/api/fileapi/ns-fileapi-by_handle_file_information
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
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

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, nint lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, nint hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static partial uint GetFinalPathNameByHandle(SafeFileHandle hFile, char* lpszFilePath, uint cchFilePath, uint dwFlags);
}
