namespace Earshot.TestWindow.Core;

// Remembers the Earshot.exe the owner chose through the file dialog, across opens. One plain text
// file, beside run-all.json in the same window-owned folder (never inside the live test root
// itself, for the same reason run-all.json is not: a stamp folder there belongs to a live test
// run, not to the window's own settings). Read back through ExePathChoice every time, so a stale
// or hand-edited file can never hand back a path that would not be accepted fresh.
internal static class ExePathSettings
{
    private const string FileName = "chosen-exe-path.txt";

    internal static string TryReadOrDefault(string dataRoot, string fallback) => TryRead(dataRoot) ?? fallback;

    internal static string? TryRead(string dataRoot)
    {
        string path = Path.Combine(dataRoot, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        string text;
        try
        {
            text = File.ReadAllText(path).Trim();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return ExePathChoice.IsValid(text, out _) ? text : null;
    }

    internal static void Write(string dataRoot, string exePath)
    {
        Directory.CreateDirectory(dataRoot);
        File.WriteAllText(Path.Combine(dataRoot, FileName), exePath);
    }
}
