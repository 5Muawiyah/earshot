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

    // Assigns a run folder its sequence number the first time anything is written for it. A plain
    // call (reissueForNewHalf false, the default) never touches an existing marker: called more
    // than once for the very same half, it must keep answering with that half's own number.
    //
    // reissueForNewHalf true is the second half actually starting: a resumed run keeps its first
    // half's run folder (same stamp), but the two halves are different events, often minutes or
    // days apart with other tests run in between. Leaving the first half's number in place would
    // make the run folder's ordering stop at whatever else was true when the FIRST half started,
    // so a second half that ends not at rest, or is killed, could read as older than a run that
    // happened between the two halves and never outrank it. The run folder's sequence must be
    // that of its latest half, so a genuine second-half start always takes a fresh number, whether
    // or not one already exists.
    internal static long EnsureMarker(string liveTestRoot, string runFolder, bool reissueForNewHalf = false)
    {
        if (!reissueForNewHalf)
        {
            long? existing = TryReadMarker(runFolder);
            if (existing is long value)
            {
                return value;
            }
        }

        long next = TakeNext(liveTestRoot);
        Directory.CreateDirectory(runFolder);
        File.WriteAllText(Path.Combine(runFolder, MarkerFileName), next.ToString(CultureInfo.InvariantCulture));
        return next;
    }

    // A non-mutating read of the counter's own current value, never incrementing or writing
    // anything: the one number RunSequence has actually issued up to. Used to tell a marker a real
    // half was assigned (never above this) apart from one a hand-forged gui-sequence.txt claims
    // that this counter never legitimately handed out. Unlike TakeNext (which must throw on a
    // corrupt counter file, since silently starting over from 0 there could reissue a number
    // already sitting on disk), a read failure here is caught, not propagated: this runs on every
    // Banner.Compute and row read, far more often than a half actually starts, and a corrupt
    // counter must never crash the window over a read-only scan. 0 is the fail-closed answer
    // either way, corrupt or genuinely never used: nothing this counter cannot itself vouch for is
    // trusted as legitimately issued.
    internal static long PeekLastIssued(string liveTestRoot)
    {
        try
        {
            return ReadLastIssued(Path.Combine(liveTestRoot, CounterFileName));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            System.Diagnostics.Trace.TraceWarning(
                "RunSequence.PeekLastIssued could not read the counter (recorded, not swallowed), treating it as 0: " + ex);
            return 0;
        }
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

    // No missing file (0, the never-before-seen case) never reads the same as a file that is
    // there but cannot be trusted: a truncated, corrupt or locked counter file used to fall back
    // to 0 here too, so the very next call reissued 1, which a marker already on disk could
    // already hold, and two different run folders ended up sharing one sequence number silently.
    // Every read failure below is left to propagate instead: the one caller, RunSequence.EnsureMarker
    // by way of MainForm's own BeginRun, already wraps that call in a try/catch that records the
    // raw exception (Trace) and marks the run Unknown rather than starting a child against a
    // counter that cannot be trusted.
    private static long ReadLastIssued(string path)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("lastIssued", out JsonElement element) &&
            element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out long value))
        {
            return value;
        }

        throw new InvalidDataException("run-sequence.json does not hold a numeric 'lastIssued': " + path);
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
