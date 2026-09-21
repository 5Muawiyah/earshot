namespace Earshot.TestWindow.Core;

// Validates the file a chooser (the file dialog, or a remembered choice read back off disk)
// offers as Earshot.exe. The same local-drive-letter rule resume.txt's own -ExePath follows
// (ResumeFile.cs): never a UNC path, and never anything relative or otherwise not rooted on a
// local drive letter.
internal static class ExePathChoice
{
    internal static bool IsValid(string path, out string? reason)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "No path was given.";
            return false;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            reason = "That is a network path, never accepted: " + path;
            return false;
        }

        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
        {
            reason = "That does not start with a local drive letter: " + path;
            return false;
        }

        if (!string.Equals(Path.GetFileName(path), "Earshot.exe", StringComparison.OrdinalIgnoreCase))
        {
            reason = "That file is not named Earshot.exe: " + path;
            return false;
        }

        if (!File.Exists(path))
        {
            reason = "That file does not exist: " + path;
            return false;
        }

        reason = null;
        return true;
    }
}
