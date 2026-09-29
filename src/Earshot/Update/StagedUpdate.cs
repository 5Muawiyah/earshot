using Earshot.Contracts;

namespace Earshot.Update;

// A verified download: the zip in a staging folder of its own under the staging root, and the SHA-256 it matched.
// Nothing here is what gets installed and nothing is held open. The staging folder is writable by the signed-in
// user, so a process running as that user can change the zip at any moment, and that is fine: the elevated update
// run copies the zip into a folder only administrators can write, hashes the copy, and goes on only if the hash is
// the one recorded here (and passed on its command line).
internal sealed class StagedUpdate
{
    private readonly ILog _log;

    internal StagedUpdate(ReleaseVersion version, string workFolder, string zipPath, string zipSha256, ILog log)
    {
        Version = version;
        WorkFolder = workFolder;
        ZipPath = zipPath;
        ZipSha256 = zipSha256;
        _log = log;
    }

    public ReleaseVersion Version { get; }

    // The staging folder made for this download, holding everything of it.
    public string WorkFolder { get; }

    // The downloaded zip.
    public string ZipPath { get; }

    // The SHA-256 the download matched, as upper-case hex.
    public string ZipSha256 { get; }

    // Deletes the whole staging folder, for an update that will not be handed over.
    public void Discard() => StagingFolders.DeleteTree(WorkFolder, _log);
}

// The folders the downloads are unpacked into, all inside one root under the data folder.
internal static class StagingFolders
{
    // A short name, because the release holds paths near the classic Windows path limit once unpacked.
    internal const string FolderPrefix = "u";

    // Deletes the folder and everything in it. A failure is logged with its raw cause and returned, never
    // swallowed: what is left behind is named.
    internal static bool DeleteTree(string path, ILog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            return true;
        }
        catch (IOException ex)
        {
            log.Warn("Update staging: " + path + " was not removed (" + ex.GetType().Name + ", HRESULT 0x" + ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + "): " + ex.Message);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            log.Warn("Update staging: " + path + " was not removed (" + ex.GetType().Name + ", HRESULT 0x" + ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + "): " + ex.Message);
            return false;
        }
    }

    // Removes the folders earlier downloads left in the staging root: the one a hand-over used stays behind after
    // the tray exits, because the tray that made it is gone by then and nothing else removes it. A folder still in use
    // is not removable and is left for the next time. Returns how many were removed.
    internal static int CleanStale(string root, ILog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(log);
        if (!Directory.Exists(root))
        {
            return 0;
        }

        int removed = 0;
        string[] folders;
        try
        {
            folders = Directory.GetDirectories(root, FolderPrefix + "*");
        }
        catch (IOException ex)
        {
            log.Warn("Update staging: " + root + " could not be listed (" + ex.GetType().Name + "): " + ex.Message);
            return 0;
        }
        catch (UnauthorizedAccessException ex)
        {
            log.Warn("Update staging: " + root + " could not be listed (" + ex.GetType().Name + "): " + ex.Message);
            return 0;
        }

        foreach (string folder in folders)
        {
            if (DeleteTree(folder, log))
            {
                removed++;
            }
        }

        return removed;
    }
}
