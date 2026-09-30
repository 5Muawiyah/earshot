using System.Security.Cryptography;
using Earshot.Contracts;

namespace Earshot.Boot.Gate;

// What a check of the installed files found. Bad holds every listed file that is missing, unreadable or not the bytes
// the release published (relative paths, as the manifest writes them); ManifestUnusable is set when there is no valid
// Earshot.files.json to check against, or it does not list Earshot.exe. Ok means a manifest was read, every file it lists
// matched, and Earshot.exe is one of them.
internal sealed record InstalledFilesReport(bool Ok, bool ManifestUnusable, IReadOnlyList<string> Bad, IReadOnlyList<StepOutcome> Steps);

// The files in the install folder against the SHA-256 values Earshot.files.json records for them. The same check runs in
// two places: the elevated repair and install-from-the-install-folder, in a folder only administrators can write, and the
// tray before it decides a repair needs bytes from the release, where it only reads. Nothing here changes a file.
internal static class InstalledFileCheck
{
    // Reads the manifest in the folder and hashes every file it lists. Every bad file is named, not only the first.
    public static InstalledFilesReport Check(string installFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installFolder);
        var steps = new List<StepOutcome>();
        FileManifest? manifest = FileManifest.Read(installFolder, out StepOutcome manifestStep);
        steps.Add(manifestStep);
        if (manifest is null)
        {
            return new InstalledFilesReport(false, true, [], steps);
        }

        var bad = new List<string>();
        bool ok = Verify(installFolder, manifest, steps, stopAtFirst: false, bad);
        bool listsProgram = manifest.HashOf(Earshot.Boot.TaskPlan.ExecutableName) is not null;
        return new InstalledFilesReport(ok, !listsProgram, bad, steps);
    }

    // Hashes each file the manifest lists, in place, and records a step for each failure with its raw code. With
    // stopAtFirst the first failure ends the check (install's own repair run); without it every file is checked.
    // Earshot.exe must be listed. Returns true when the manifest lists it and every listed file matched.
    internal static bool Verify(string install, FileManifest manifest, List<StepOutcome> steps, bool stopAtFirst, List<string>? bad = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(steps);
        if (manifest.HashOf(Earshot.Boot.TaskPlan.ExecutableName) is null)
        {
            steps.Add(StepOutcomes.NotAttempted("verify-installed", Earshot.Boot.TaskPlan.ExecutableName + " is not in the published file list."));
            return false;
        }

        bool all = true;
        foreach (ManifestFile file in manifest.Files)
        {
            string path = Path.Combine(install, file.RelativePath);
            string actual;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
                actual = Convert.ToHexString(SHA256.HashData(stream));
            }
            catch (IOException ex)
            {
                steps.Add(StepOutcomes.FromHResult("verify-installed:" + file.RelativePath, ex.HResult, path + ": " + ex.Message));
                bad?.Add(file.RelativePath);
                all = false;
                if (stopAtFirst)
                {
                    return false;
                }

                continue;
            }
            catch (UnauthorizedAccessException ex)
            {
                steps.Add(StepOutcomes.FromHResult("verify-installed:" + file.RelativePath, ex.HResult, path + ": " + ex.Message));
                bad?.Add(file.RelativePath);
                all = false;
                if (stopAtFirst)
                {
                    return false;
                }

                continue;
            }

            if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                steps.Add(StepOutcomes.NotAttempted("verify-installed:" + file.RelativePath,
                    "The installed file does not match the hash recorded when it was published. " + FileManifest.MissingMessage));
                bad?.Add(file.RelativePath);
                all = false;
                if (stopAtFirst)
                {
                    return false;
                }
            }
        }

        if (all)
        {
            steps.Add(new StepOutcome("verify-installed", true, 0, "S_OK", manifest.Files.Count + " installed files match the published hashes."));
        }

        return all;
    }
}
