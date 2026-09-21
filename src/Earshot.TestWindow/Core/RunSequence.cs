using System.Globalization;
using System.Text.Json;

namespace Earshot.TestWindow.Core;

// A monotonically increasing counter, immune to the system clock: with the clock stepped back
// between two runs, both a run folder's own stamp and result.json's own finishedUtc can make a
// chronologically later run look older than an earlier one, hiding a later kill or a later
// not-at-rest result behind it, or letting a later fail read as the chosen (and reported "Passed")
// run. Persisted beside run-all.json (never inside a stamp folder, since it spans every run), one
// file, read-modify-write atomically so two calls never hand out the same value and a crash
// between them never loses or reuses one.
internal static class RunSequence
{
    internal const string CounterFileName = "run-sequence.json";

    // gui- prefixed like the window's other markers, and additive only: never touching what the
    // scripts write. Holds the plain decimal number this run folder was issued, nothing else.
    internal const string MarkerFileName = "gui-sequence.txt";

    // The next value, persisted before it is ever handed back: by the time this call returns, the
    // counter file on disk already reflects it, so a crash right after this call, or two callers
    // racing, can never hand out the same value twice or lose one that was already issued.
    internal static long TakeNext(string liveTestRoot)
    {
        Directory.CreateDirectory(liveTestRoot);
        string path = Path.Combine(liveTestRoot, CounterFileName);
        long next = ReadLastIssued(path) + 1;
        WriteAtomic(path, next);
        return next;
    }

    // Assigns a run folder its sequence number the first time anything is written for it, and
    // never again: a resumed second half reuses the same run folder and must never be issued a
    // fresh number just for resuming into it, or ordering between the first and second half of
    // the very same run would itself become meaningless.
    internal static long EnsureMarker(string liveTestRoot, string runFolder)
    {
        long? existing = TryReadMarker(runFolder);
        if (existing is long value)
        {
            return value;
        }

        long next = TakeNext(liveTestRoot);
        Directory.CreateDirectory(runFolder);
        File.WriteAllText(Path.Combine(runFolder, MarkerFileName), next.ToString(CultureInfo.InvariantCulture));
        return next;
    }

    // Null when the folder has no marker (an older run, from before this feature existed, or one
    // this window never started) or the marker cannot be parsed as a whole number: never guessed,
    // the caller falls back to time ordering for a folder this returns null for.
    internal static long? TryReadMarker(string runFolder)
    {
        string path = Path.Combine(runFolder, MarkerFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            string text = File.ReadAllText(path).Trim();
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long ReadLastIssued(string path)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("lastIssued", out JsonElement element) &&
                element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out long value))
            {
                return value;
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return 0;
    }

    // Write to a temp file in the same folder, then rename over the real one: File.Move's own
    // three-argument overload (overwrite: true) is an atomic rename on the same volume, so a
    // reader (or a crash) never observes a partially written counter file, only the old value or
    // the new one, never a corrupt in-between.
    private static void WriteAtomic(string path, long lastIssued)
    {
        string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string json = "{\"lastIssued\":" + lastIssued.ToString(CultureInfo.InvariantCulture) + "}";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }
}
