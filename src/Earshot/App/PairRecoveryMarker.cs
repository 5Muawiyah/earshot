using System.Globalization;
using System.Text;
using Earshot.Contracts;

namespace Earshot.App;

// A Pair AirPods run between its pairing and its adoption. Pairing makes the AirPods a Windows device at once, and adoption
// (the pin, and the gate's block once Earshot is set up) comes after; a crash, a sign-out or a shutdown in between would leave
// new AirPods paired, unpinned and unblocked with nothing to say so. So the tray writes this marker before the pairing starts
// and clears it once the pair is adopted or the pairing ended without AirPods to adopt. A marker found when the tray starts is
// a run that did not finish: the tray then looks at the pin (the stale-pin check) and offers the device picker for the pair
// that was left, and the pair is blocked through the usual path when the person chooses it and Earshot is set up. Nothing is
// unpaired, adopted or blocked by the marker itself.
//
// %LOCALAPPDATA%\Earshot\pair-pending.json, per user like the pairing. It holds a time and a process id, no device.
internal interface IPairRecoveryMarker
{
    // Before the pairing. A marker that could not be written is logged by the caller with its code; the pairing still runs,
    // since not pairing would help nobody.
    StepOutcome Write();

    StepOutcome Clear();

    // True when a marker is there. The tray is one per session, so a marker seen at its start is one an earlier tray left.
    bool Exists();
}

internal sealed class NoPairRecoveryMarker : IPairRecoveryMarker
{
    public static NoPairRecoveryMarker Instance { get; } = new();

    public StepOutcome Write() => StepOutcomes.NotAvailable("pair-marker-write", "No marker in this tray.");

    public StepOutcome Clear() => StepOutcomes.NotAvailable("pair-marker-clear", "No marker in this tray.");

    public bool Exists() => false;
}

internal sealed class PairRecoveryMarker : IPairRecoveryMarker
{
    public const string FileName = "pair-pending.json";

    private readonly string _file;
    private readonly TimeProvider _time;

    public PairRecoveryMarker(string localFolder, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localFolder);
        ArgumentNullException.ThrowIfNull(time);
        _file = Path.Combine(localFolder, FileName);
        _time = time;
    }

    public StepOutcome Write()
    {
        string text = "{\"OpenedUtc\":\"" + _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture) + "\",\"ProcessId\":" +
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "}";
        try
        {
            string? folder = Path.GetDirectoryName(_file);
            if (folder is not null)
            {
                Directory.CreateDirectory(folder);
            }

            string temp = _file + ".tmp";
            File.WriteAllBytes(temp, Encoding.UTF8.GetBytes(text));
            File.Move(temp, _file, overwrite: true);
            return new StepOutcome("pair-marker-write", true, 0, "ERROR_SUCCESS", null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StepOutcomes.FromHResult("pair-marker-write", ex.HResult, ex.GetType().Name + ": " + ex.Message);
        }
    }

    public StepOutcome Clear()
    {
        try
        {
            File.Delete(_file);
            return new StepOutcome("pair-marker-clear", true, 0, "ERROR_SUCCESS", null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StepOutcomes.FromHResult("pair-marker-clear", ex.HResult, ex.GetType().Name + ": " + ex.Message);
        }
    }

    public bool Exists() => File.Exists(_file);
}
