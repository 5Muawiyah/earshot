using System.Globalization;
using System.Security.Cryptography;
using Earshot.Contracts;

namespace Earshot.Boot.Gate;

internal sealed record CopiedFile(string RelativePath, long Length, string Sha256);

internal sealed record IntegrityCopyResult(bool Ok, IReadOnlyList<CopiedFile> Files, IReadOnlyList<StepOutcome> Steps);

// Copies the application folder for install and proves the copy is what was read.
//
// The unzip folder is user-writable, so a process running as the user could swap a file while the elevated
// install copies it. Every source file is opened first and held open with FileShare.Read for the whole
// copy, which denies any other writer, rename or delete (no FileShare.Write, no FileShare.Delete). Each file
// is hashed with SHA-256 from its open handle, copied from the same handle while the bytes are hashed
// again, and the destination is re-read and hashed a third time. Any difference, a reparse point anywhere
// in the source, or any I/O failure fails the whole copy; the caller then removes the destination.
// https://learn.microsoft.com/en-us/dotnet/api/system.io.fileshare
// https://learn.microsoft.com/en-us/windows/win32/fileio/creating-and-opening-files
// https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.sha256
internal static class IntegrityCopy
{
    public const int MaxFiles = 5000;
    private const int BufferSize = 81920;

    public static IntegrityCopyResult Copy(string sourceFolder, string destinationFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFolder);
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceFolder));
        string destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationFolder));
        var steps = new List<StepOutcome>();
        var copied = new List<CopiedFile>();
        var opened = new List<(string Relative, FileStream Stream)>();

        try
        {
            if (IsInside(destination, source) || IsInside(source, destination))
            {
                steps.Add(StepOutcomes.NotAttempted("copy-app", "The source and destination folders overlap."));
                return new IntegrityCopyResult(false, copied, steps);
            }

            if (!TryListFiles(source, steps, out List<string> relativeFiles))
            {
                return new IntegrityCopyResult(false, copied, steps);
            }

            // Hold every source file before reading any of them.
            foreach (string relative in relativeFiles)
            {
                string path = Path.Combine(source, relative);
                opened.Add((relative, new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan)));
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
                copied.Count.ToString(CultureInfo.InvariantCulture) + " files copied and verified with SHA-256."));
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
        }

        return new IntegrityCopyResult(false, copied, steps);
    }

    // Lists every file under root as a relative path. Fails on any reparse point (a junction or link could
    // make the copy read from somewhere else) and on more than MaxFiles files.
    private static bool TryListFiles(string root, List<StepOutcome> steps, out List<string> files)
    {
        files = [];
        var rootInfo = new DirectoryInfo(root);
        if (!rootInfo.Exists)
        {
            steps.Add(StepOutcomes.FromHResult("copy-app", unchecked((int)0x80070003), "The source folder does not exist: " + root));
            return false;
        }

        if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            steps.Add(StepOutcomes.NotAttempted("copy-app", "The source folder is a reparse point."));
            return false;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
        };
        var pending = new Stack<DirectoryInfo>();
        pending.Push(rootInfo);
        while (pending.Count > 0)
        {
            DirectoryInfo current = pending.Pop();
            foreach (FileSystemInfo entry in current.EnumerateFileSystemInfos("*", options))
            {
                string relative = Path.GetRelativePath(root, entry.FullName);
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    steps.Add(StepOutcomes.NotAttempted("copy-app:" + relative, "A reparse point in the source is not copied."));
                    return false;
                }

                if (entry is DirectoryInfo directory)
                {
                    pending.Push(directory);
                }
                else
                {
                    files.Add(relative);
                    if (files.Count > MaxFiles)
                    {
                        steps.Add(StepOutcomes.NotAttempted("copy-app", "The source has more than " + MaxFiles + " files."));
                        return false;
                    }
                }
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return true;
    }

    internal static bool IsInside(string path, string folder)
    {
        string p = Path.TrimEndingDirectorySeparator(path);
        string f = Path.TrimEndingDirectorySeparator(folder);
        return p.Equals(f, StringComparison.OrdinalIgnoreCase) ||
               p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
