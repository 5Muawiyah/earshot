using System.Globalization;

namespace Earshot.Widget;

// What the rule makes of a leave Earshot did not start.
internal enum MovedVerdictKind
{
    // The pair kept sending, out of the case and near: another device has them.
    Moved,

    // A case-known message of the pair near the leave: they went into the case.
    IntoCase,

    // Too few messages, or too weak, by the bound: out of range, or nothing to say.
    NotHeard,

    // This PC had the AirPods again first.
    BackHere,
}

// The messages a verdict rests on, as offsets from the leave. Messages counts the case-unknown ones after the leave up to the
// verdict; CaseKnown counts the case-known ones from CaseLookBack before the leave up to it. First and Last are the offsets of the
// first and last counted message, and MedianDbm their median signal; all three are zero with no message. Numbers only: no address.
internal sealed record MovedSummary(int Messages, TimeSpan First, TimeSpan Last, double MedianDbm, int CaseKnown);

internal sealed record MovedVerdict(MovedVerdictKind Kind, MovedSummary Summary, TimeSpan After)
{
    // "Move: 3 messages by +3800 ms, first +900 ms, median -62 dBm, case-known 0: moved."
    public string Line()
    {
        MovedSummary s = Summary;
        string words = Kind switch
        {
            MovedVerdictKind.Moved => "moved",
            MovedVerdictKind.IntoCase => "into the case",
            MovedVerdictKind.NotHeard => "not heard",
            _ => "back on this PC",
        };

        string body = Count(s.Messages) + " by +" + Ms(After) + " ms" +
            (s.Messages > 0 ? ", first +" + Ms(s.First) + " ms, median " + Dbm(s.MedianDbm) + " dBm" : string.Empty) +
            ", case-known " + s.CaseKnown.ToString(CultureInfo.InvariantCulture);
        return "Move: " + body + ": " + words + ".";
    }

    // What the thirty seconds after the leave held, so a longer bound can be judged afterwards.
    public static string LongLine(MovedSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return "Move: at +" + Ms(MovedToRule.LongLineAfter) + " ms: " + Count(summary.Messages) +
            (summary.Messages > 0 ? ", median " + Dbm(summary.MedianDbm) + " dBm" : string.Empty) +
            ", case-known " + summary.CaseKnown.ToString(CultureInfo.InvariantCulture) + ".";
    }

    private static string Count(int messages) =>
        messages.ToString(CultureInfo.InvariantCulture) + (messages == 1 ? " message" : " messages");

    private static string Ms(TimeSpan span) => ((long)span.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);

    private static string Dbm(double value) => Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
}

// Whether the AirPods were taken by another device, read from the broadcast of the owner's pair alone. The documented message has
// no in-ear bit, so "moved" is the pair still sending, out of the case and near, after a leave Earshot did not start. It cannot
// tell an iPhone from earbuds that are out and idle; the card says "Moved to" and the owner's own answer is what live test 25 records.
//
// Pure: it is handed the messages (Observe), the leave (Left) and the time (Check), and keeps no clock. Not thread safe; the
// service calls it under its own lock.
//
// Every figure below is a design choice, not a measurement; no trial was run. Sources:
//  - MovedBound, 10 s: BroadcastRules.Window holds about six messages at the in-use rate of one every 1.7 s, so the messages below
//    are normally met in about 4 s and the bound leaves room for a quiet stretch.
//  - MovedMessages, 3, and MovedSpan, 2 s: BroadcastRules.RefindMessages and RefindSpan, for the stated reason there: a median
//    rather than a passer-by, over a span one stray message cannot fill.
//  - MovedNearDbm, -80: a pair worn nearby read -64 to -76 on 1 October, plus 4 dB. The signal at the edge of range is unmeasured.
//  - CaseLookBack, 10 s: a case-known message 2 s and 5 s before a leave was seen in 2 of the 8 recorded leaves.
//  - LongLineAfter, 30 s: only a log line, so a longer bound can be judged afterwards.
// Live test 25 and the log lines measure all of them.
internal sealed class MovedToRule
{
    public static readonly TimeSpan MovedBound = TimeSpan.FromSeconds(10);

    public const int MovedMessages = 3;

    public static readonly TimeSpan MovedSpan = TimeSpan.FromSeconds(2);

    public const double MovedNearDbm = -80;

    public static readonly TimeSpan CaseLookBack = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan LongLineAfter = TimeSpan.FromSeconds(30);

    private readonly record struct Sample(DateTimeOffset At, sbyte Rssi, bool CaseKnown);

    private readonly List<Sample> _samples = new();
    private DateTimeOffset? _left;
    private bool _verdictGiven;
    private bool _longTaken;

    // How many messages are held, for tests.
    internal int HeldForTest => _samples.Count;

    // A leave is being watched and has no verdict yet.
    public bool Watching => _left is not null && !_verdictGiven;

    // The next time Check (or TakeLongSummary) has something to say with no message to prompt it: the bound, then the long line.
    public DateTimeOffset? NextDue => _left is not { } left
        ? null
        : !_verdictGiven ? left + MovedBound : !_longTaken ? left + LongLineAfter : null;

    // One message of the paired model (and the held colour), whether or not the set is linked.
    public void Observe(DateTimeOffset at, sbyte rssi, bool caseKnown)
    {
        _samples.Add(new Sample(at, rssi, caseKnown));

        // The window and the bound (BroadcastRules.Window plus MovedBound), or more while a watch needs the look-back and the
        // thirty seconds after the leave; never more than BroadcastRules.MaxRefindSamples.
        DateTimeOffset cutoff = at - (BroadcastRules.Window + MovedBound);
        if (_left is { } left && !_longTaken && left - CaseLookBack < cutoff)
        {
            cutoff = left - CaseLookBack;
        }

        _samples.RemoveAll(s => s.At < cutoff);
        while (_samples.Count > BroadcastRules.MaxRefindSamples)
        {
            _samples.RemoveAt(0);
        }
    }

    // The AirPods left this PC and Earshot did not start it: a watch begins, in place of any before it.
    public void Left(DateTimeOffset at)
    {
        _left = at;
        _verdictGiven = false;
        _longTaken = false;
    }

    // Everything is forgotten: a different pair was adopted, so its messages and its leave say nothing about the new one.
    public void Reset()
    {
        _samples.Clear();
        _left = null;
        _verdictGiven = false;
        _longTaken = false;
    }

    // The verdict, once, or null while it is not yet to be had. backOnThisPc: this PC had the AirPods again since the leave.
    public MovedVerdict? Check(DateTimeOffset now, bool backOnThisPc)
    {
        if (_left is not { } left || _verdictGiven)
        {
            return null;
        }

        DateTimeOffset bound = left + MovedBound;
        DateTimeOffset end = now < bound ? now : bound;
        MovedSummary summary = Summarise(left, end);
        TimeSpan after = now - left;
        if (backOnThisPc)
        {
            return Give(MovedVerdictKind.BackHere, summary, after);
        }

        if (summary.CaseKnown > 0)
        {
            return Give(MovedVerdictKind.IntoCase, summary, after);
        }

        if (summary.Messages >= MovedMessages && summary.Last - summary.First >= MovedSpan && summary.MedianDbm >= MovedNearDbm)
        {
            return Give(MovedVerdictKind.Moved, summary, after);
        }

        return now >= bound ? Give(MovedVerdictKind.NotHeard, summary, after) : null;
    }

    // What the thirty seconds after the leave held, once, when they have passed and the verdict was given; else null.
    public MovedSummary? TakeLongSummary(DateTimeOffset now)
    {
        if (_left is not { } left || !_verdictGiven || _longTaken || now < left + LongLineAfter)
        {
            return null;
        }

        _longTaken = true;
        return Summarise(left, left + LongLineAfter);
    }

    private MovedVerdict Give(MovedVerdictKind kind, MovedSummary summary, TimeSpan after)
    {
        _verdictGiven = true;
        return new MovedVerdict(kind, summary, after);
    }

    // The case-unknown messages from the leave to end, and the case-known ones from CaseLookBack before the leave to end.
    private MovedSummary Summarise(DateTimeOffset left, DateTimeOffset end)
    {
        var levels = new List<double>();
        DateTimeOffset? first = null;
        DateTimeOffset? last = null;
        int caseKnown = 0;
        foreach (Sample s in _samples)
        {
            if (s.At > end)
            {
                continue;
            }

            if (s.CaseKnown)
            {
                if (s.At >= left - CaseLookBack)
                {
                    caseKnown++;
                }

                continue;
            }

            if (s.At < left)
            {
                continue;
            }

            levels.Add(s.Rssi);
            first = first is { } f && f <= s.At ? f : s.At;
            last = last is { } l && l >= s.At ? l : s.At;
        }

        if (levels.Count == 0)
        {
            return new MovedSummary(0, TimeSpan.Zero, TimeSpan.Zero, 0, caseKnown);
        }

        levels.Sort();
        int mid = levels.Count / 2;
        double median = levels.Count % 2 == 1 ? levels[mid] : (levels[mid - 1] + levels[mid]) / 2;
        return new MovedSummary(levels.Count, first!.Value - left, last!.Value - left, median, caseKnown);
    }
}
