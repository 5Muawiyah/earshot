using Earshot.Contracts;

namespace Earshot.Update;

// A verified download, unpacked into a folder of its own under the staging root, with every unpacked file held
// open for reading (no other process can write to it, replace it or delete it) from the moment it was hashed until
// this is disposed. The hand-over runs the staged Earshot.exe elevated from a folder the signed-in user can write
// to, so the files are held from the check against the release's own file list until the elevated program has
// started; a process that ends releases them.
internal sealed class StagedUpdate : IDisposable
{
    private readonly List<FileStream> _held;
    private readonly ILog _log;
    private bool _disposed;

    internal StagedUpdate(ReleaseVersion version, string workFolder, string appFolder, string executablePath, List<FileStream> held, ILog log)
    {
        Version = version;
        WorkFolder = workFolder;
        AppFolder = appFolder;
        ExecutablePath = executablePath;
        _held = held;
        _log = log;
    }

    public ReleaseVersion Version { get; }

    // The staging folder made for this download, holding everything of it.
    public string WorkFolder { get; }

    // The unpacked application folder, the one that holds Earshot.exe and Earshot.files.json.
    public string AppFolder { get; }

    public string ExecutablePath { get; }

    // Lets go of the files without deleting them.
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (FileStream stream in _held)
        {
            stream.Dispose();
        }

        _held.Clear();
    }

    // Lets go of the files and deletes the whole staging folder, for an update that will not be handed over.
    public void Discard()
    {
        Dispose();
        StagingFolders.DeleteTree(WorkFolder, _log);
    }
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
    // the tray exits, because the program running from it cannot delete it. A folder still in use (the elevated
    // program is running from it) is not removable and is left for the next time. Returns how many were removed.
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
