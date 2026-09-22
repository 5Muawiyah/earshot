using System.Drawing;

namespace Earshot.TestWindow.Core;

// Loads a how-to picture by name from Data\pictures\<name>.png, copied beside the exe the same
// way Data\wording.json is. A missing or unreadable file shows nothing on screen, never an error:
// HowToBlockTests.EveryPictureNamedInWordingJsonExistsOnDisk is what catches a missing file, listed for the writer, not a runtime
// exception here. Reads the bytes first and decodes from memory, rather than Image.FromFile, so
// the file itself is never left open by a live Image the caller forgets to dispose promptly.
internal static class HowToPictures
{
    internal static string FolderPath() => Path.Combine(AppContext.BaseDirectory, "Data", "pictures");

    internal static Image? TryLoad(string name)
    {
        string path = Path.Combine(FolderPath(), name + ".png");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            return Image.FromStream(new MemoryStream(bytes));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            // GDI+'s own signal that the bytes are not a valid image.
            return null;
        }
    }
}
