using System.Diagnostics;
using Earshot.Contracts;

namespace Earshot.Boot;

// One Earshot.exe on disk: whether the file is there and, when it is, the file version it carries. Version is null
// when the file is missing, cannot be read or holds no version; Step records how the read went, with the raw code.
internal sealed record InstalledFile(bool Present, Version? Version, StepOutcome Step);

// Reads the file version of an Earshot.exe, so the tray can tell an install that lost its file, and a copy that is
// newer than the one installed, without asking Task Scheduler or elevating.
internal interface IInstalledFiles
{
    InstalledFile Read(string path);
}

internal sealed class InstalledFileReader : IInstalledFiles
{
    private const int ErrorFileNotFound = unchecked((int)0x80070002);

    public InstalledFile Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        const string step = "read-file-version";
        try
        {
            if (!File.Exists(path))
            {
                return new InstalledFile(false, null, StepOutcomes.FromHResult(step, ErrorFileNotFound, "Missing: " + path, ok: false));
            }

            // https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.fileversioninfo.getversioninfo
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            if (info.FileVersion is null)
            {
                return new InstalledFile(true, null, StepOutcomes.NotAvailable(step, path + " carries no file version."));
            }

            var version = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
            return new InstalledFile(true, version, new StepOutcome(step, true, 0, "S_OK", path + " " + version));
        }
        catch (FileNotFoundException ex)
        {
            return new InstalledFile(false, null, StepOutcomes.FromHResult(step, ex.HResult, "Missing: " + path, ok: false));
        }
        catch (IOException ex)
        {
            return new InstalledFile(true, null, StepOutcomes.FromHResult(step, ex.HResult, path + ": " + ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            return new InstalledFile(true, null, StepOutcomes.FromHResult(step, ex.HResult, path + ": " + ex.Message));
        }
    }
}
