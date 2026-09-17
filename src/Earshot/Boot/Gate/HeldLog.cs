using System.Diagnostics;
using System.Globalization;
using Earshot.Contracts;
using Earshot.Infra;

namespace Earshot.Boot.Gate;

// The log of an elevated install or uninstall. Both start from the user's own session, and that user can change
// everything under their profile, so an elevated write there could follow a junction or link the user planted to
// any file: FileLog creates the folder and appends by path. So nothing is written to a file while the run goes on.
// Each entry goes to the debugger output at once and is kept, with its time, up to MaxEntries. Once the run has
// ended, FlushTo writes the kept entries to a log in a folder only administrators can change (Program.MachineLog),
// or, when there is none, leaves them in the debugger output and says why there.
internal sealed class HeldLog : ILog
{
    internal const int MaxEntries = 4000;

    private readonly string _mode;
    private readonly Lock _gate = new();
    private readonly List<HeldEntry> _entries = new();
    private int _dropped;

    public HeldLog(string mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        _mode = mode;
    }

    // Entries kept and not yet written.
    internal int Count
    {
        get { lock (_gate) { return _entries.Count; } }
    }

    public void Write(LogLevel level, string message, Exception? ex = null)
    {
        DateTime now = DateTime.UtcNow;
        Trace.WriteLine("Earshot " + _mode + " " + level + " " + message + (ex is null ? "" : " " + ex));
        lock (_gate)
        {
            if (_entries.Count < MaxEntries)
            {
                _entries.Add(new HeldEntry(now, level, message, ex));
            }
            else
            {
                _dropped++;
            }
        }
    }

    // Writes every kept entry to target, in order and with the time it was logged, then forgets them. With no target
    // they stay in the debugger output only, and whyNoFile says why there.
    public void FlushTo(FileLog? target, string whyNoFile)
    {
        List<HeldEntry> entries;
        int dropped;
        lock (_gate)
        {
            entries = [.. _entries];
            dropped = _dropped;
            _entries.Clear();
            _dropped = 0;
        }

        if (target is null)
        {
            Trace.WriteLine("Earshot " + _mode + ": no log file was written, because " + whyNoFile + ".");
            return;
        }

        foreach (HeldEntry entry in entries)
        {
            target.WriteAt(entry.Utc, entry.Level, entry.Message, entry.Exception);
        }

        if (dropped > 0)
        {
            target.Write(LogLevel.Warn, _mode + ": " + dropped.ToString(CultureInfo.InvariantCulture) +
                " more entries went to the debugger output only (" + MaxEntries.ToString(CultureInfo.InvariantCulture) + " are kept).");
        }
    }

    private readonly record struct HeldEntry(DateTime Utc, LogLevel Level, string Message, Exception? Exception);
}
