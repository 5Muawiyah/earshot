using System.Globalization;
using Earshot.Contracts;

namespace Earshot.Tracing;

internal enum TraceOutcome
{
    // The trace is saved and the tray has it (or is about to).
    Saved,

    // The trace is saved but stays in the administrators-only folder: the tray was gone, or the file could not be made readable.
    SavedInPlace,

    // The tray exited while the recording ran, so nobody would collect a file: the session was cancelled and nothing was saved.
    Discarded,

    // Helper side only, after the tray copied the trace: the helper's own copy in the administrators-only folder could not be
    // deleted. The helper exits with ExitCodes.IoError and the tray says where that copy is.
    CopyKept,

    Refused,
    WprMissing,
    ProfileChanged,
    SetupFailed,
    StartFailed,
    StopFailed,

    // Tray side only: the person said no at the administrator prompt.
    Declined,

    // Tray side only: the helper ended without saying it was done.
    HelperFailed,
}

internal enum TraceStopReason
{
    StopRequested,
    TrayExited,
    TimedOut,
}

internal enum TraceState
{
    Idle,
    Recording,
}

// What the tray learns when a recording ends. FolderPath and FilePath are set when a file was saved.
internal sealed record TraceResult(TraceOutcome Outcome, string Message, string? FolderPath, string? FilePath);

// The command line of the elevated helper: trace <tray pid> <nonce>. Anything else is rejected before anything is read.
internal sealed record TraceRequest(int TrayPid, string Nonce)
{
    public const string Verb = "trace";

    public const string Usage = "Usage: trace <tray process id> <32 hex characters>";

    public static bool TryParse(IReadOnlyList<string> args, out TraceRequest? request, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(args);
        request = null;
        if (args.Count != 3 || !string.Equals(args[0], Verb, StringComparison.Ordinal))
        {
            problem = "Expected exactly " + Verb + " <pid> <nonce>.";
            return false;
        }

        if (!int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0)
        {
            problem = "The process id is not a positive number.";
            return false;
        }

        if (!BoundaryValidation.IsNonce(args[2]))
        {
            problem = "The nonce is not 32 lower-case hex characters.";
            return false;
        }

        request = new TraceRequest(pid, args[2]);
        problem = null;
        return true;
    }
}

// result.txt in the run folder: what the helper tells the tray. Plain lines, parsed strictly. The file name must be a bare
// name, so a result cannot point the tray's copy at another folder.
internal sealed record TraceResultFile(TraceOutcome Outcome, string FileName, int WprCode)
{
    public static string Serialize(TraceResultFile result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return "outcome=" + result.Outcome + "\nfile=" + result.FileName + "\nwpr=" + result.WprCode.ToString(CultureInfo.InvariantCulture) + "\n";
    }

    public static bool TryParse(string? text, out TraceResultFile? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string? outcomeText = null;
        string? file = null;
        string? wpr = null;
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.TrimEnd('\r');
            int eq = trimmed.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0)
            {
                return false;
            }

            string key = trimmed[..eq];
            string value = trimmed[(eq + 1)..];
            switch (key)
            {
                case "outcome": outcomeText = value; break;
                case "file": file = value; break;
                case "wpr": wpr = value; break;
                default: return false;
            }
        }

        if (outcomeText is null || file is null || wpr is null
            || !Enum.TryParse(outcomeText, ignoreCase: false, out TraceOutcome outcome) || !Enum.IsDefined(outcome)
            || !int.TryParse(wpr, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int code)
            || !IsBareName(file))
        {
            return false;
        }

        result = new TraceResultFile(outcome, file, code);
        return true;
    }

    // Empty is allowed (no file was saved); otherwise a name with no folder part.
    internal static bool IsBareName(string name) =>
        name.Length == 0
        || (name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name is not "." and not ".." && !name.Contains(':', StringComparison.Ordinal));
}

// running.txt in the trace folder, while a helper has a session open. A marker whose helper is gone means the session may
// still be recording: it runs until stopped or the PC restarts.
internal sealed record TraceMarker(int HelperPid, string Nonce)
{
    public static string Serialize(TraceMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        return "helper=" + marker.HelperPid.ToString(CultureInfo.InvariantCulture) + "\nnonce=" + marker.Nonce + "\n";
    }

    public static bool TryParse(string? text, out TraceMarker? marker)
    {
        marker = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        int? pid = null;
        string? nonce = null;
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith("helper=", StringComparison.Ordinal)
                && int.TryParse(trimmed.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0)
            {
                pid = value;
            }
            else if (trimmed.StartsWith("nonce=", StringComparison.Ordinal) && BoundaryValidation.IsNonce(trimmed[6..]))
            {
                nonce = trimmed[6..];
            }
            else
            {
                return false;
            }
        }

        if (pid is null || nonce is null)
        {
            return false;
        }

        marker = new TraceMarker(pid.Value, nonce);
        return true;
    }
}

// The words the person reads. British English, plain, no em-dashes.
internal static class TraceCopy
{
    public const string StartButton = "Record a Bluetooth trace";
    public const string StopButton = "Stop";
    public const string Running = "Recording a Bluetooth trace. Try pairing now.";
    // Shown before anything is asked or recorded, with the button that starts the recording, and the folder it will be saved in.
    public static string NoteFor(string folder) =>
        "A Bluetooth trace holds the Bluetooth addresses of your devices and may hold pairing keys. It is saved in " + folder +
        ". Earshot never sends it anywhere. Share it only with someone you trust, and delete it when you're done.";

    public const string NoteButton = "Record";
    public const string SetUpFirst = "Set up or update Earshot first, then record a Bluetooth trace.";
    public const string Untrusted = "Earshot's trace folder under ProgramData can be changed by more than administrators, so nothing was copied from it.";

    public static string NotKept(string folder, string problem) =>
        "The trace was recorded but could not be copied to " + folder + " (" + problem + "), so it was not kept. Try again.";

    public static string Leftover(int count) =>
        "The Earshot\\trace folder under ProgramData still holds " + count.ToString(CultureInfo.InvariantCulture) +
        " earlier Bluetooth trace folder(s), which only administrators can open. They may hold pairing keys; an administrator can delete them.";
    public const string AdminCopyKept = "Earshot's own copy could not be deleted. It is in the Earshot\\trace folder under ProgramData, which only administrators can open.";

    public static string SavedIn(string folder) => "Trace saved in " + folder + ". Delete it when you're done.";
    public const string WprMissing = "Windows Performance Recorder isn't on this PC.";
    public const string Declined = "The Windows prompt was declined, so no trace was recorded.";
    public const string SafeMode = "A Bluetooth trace can't be recorded in safe mode.";
    public const string AlreadyRecording = "A Bluetooth trace is already being recorded.";
    public const string StillRunning = "A Bluetooth trace may still be running. Restart the PC to stop it.";

    public static string Encoded(int code) => "0x" + code.ToString("x8", CultureInfo.InvariantCulture);
}
