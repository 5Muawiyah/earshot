namespace Earshot.TestWindow.Core;

// Remembers whether "Show technical details" is on, across opens, beside the window's other
// settings (chosen-exe-path.txt, run-all.json) in the same window-owned folder, never inside the
// live test root itself, for the same reason those are not. Off is the default: a fresh window, a
// missing file, or one that fails to read or does not hold a recognised value, all start with the
// script's own words hidden, never shown by accident.
internal static class TechnicalDetailsSettings
{
    private const string FileName = "show-technical-details.txt";

    internal static bool TryReadOrDefault(string dataRoot) => TryRead(dataRoot) ?? false;

    internal static bool? TryRead(string dataRoot)
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

        return text switch
        {
            "1" => true,
            "0" => false,
            _ => null,
        };
    }

    internal static void Write(string dataRoot, bool value)
    {
        Directory.CreateDirectory(dataRoot);
        File.WriteAllText(Path.Combine(dataRoot, FileName), value ? "1" : "0");
    }
}
