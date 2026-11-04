using System.Globalization;

namespace Earshot.Widget;

// What happened to the linked pair's case at one message, for the case-open card.
internal enum CaseOpenStep { None, Opened }

// Whether the linked pair's case is open, worked out from the linked pair's own messages only. Pure: the time is handed in,
// and WidgetStatusService feeds it every message of the linked set (and of no other) under its lock.
//
// An open is either of two things, and nothing else.
//  - The message that makes the link (BroadcastSelector.TryLink), whose own rule has already seen a burst: that is the case
//    opening next to the PC.
//  - While closed, the linked set's case-known messages reaching OpenMessages inside OpenWindow, the first and the last at
//    least OpenSpan apart, merged across the pair's two senders. The same bar a link asks (BroadcastRules.LinkMessages,
//    LinkWindow, LinkSpan), for the same reason: one stray message, or the few a pair sends in passing, cannot make it.
//
// Why not any case-known message after silence: the saved records (scratch\one-card\evidence.md) show seven links, each
// followed about a minute after the first close by one or two case-known messages of the linked set's own address, then
// nothing; the card showed again for eight seconds each time. So a shut case is not silent: it sends a lone pair about a
// minute after it shuts. Why not the lid byte: in the 30 September capture both senders moved it through
// four values (0x51, 0x59, 0x52, 0x5A) inside one unbroken run of messages, so a change of it is not a reopen. The byte is
// logged, never read.
//
// A case-unknown message never counts: a pair in use sends the case level as unknown (0xF), so buds being worn never show the
// card.
//
// A close is the linked pair's case-known messages stopping for CloseAfter. No source documents a bit that says the lid is
// shut, so the cadence is what closes it, and a card can outlast a shut lid by CloseAfter. A reopen inside CloseAfter is not a
// new open (the messages never stopped), and a lid opened for under OpenSpan while closed shows nothing.
//
// Every open, every burst that did not open, and every close adds one line to the log (TakeLines), never one per message and
// never an address: the morning test reads each show and the messages behind it.
internal sealed class CaseOpenTracker
{
    // How long without a case-known message of the linked pair before the case counts as closed. A design choice, from the
    // cadence the saved records show: with the lid open the set sends about four case-known messages a second between its two
    // buds; the longest gap between two messages of a merged set in any record is 3.11 s, and between two messages of one
    // sender 6.14 s (BatteryFreshness.FreshWindow, WidgetTiming.RefreshWindow cite the same figures). With one bud in the case
    // and one out, only that bud's sender may give the case level, so the single-sender gap is the one to clear: 8 s is about
    // 30% over 6.14 s and over two and a half times the set's 3.11 s, so a slow sender does not close an open case, while a
    // shut case closes the card 8 s after its last message (a timer due at CloseDueAt notices it, since silence brings no
    // message).
    public static readonly TimeSpan CloseAfter = TimeSpan.FromSeconds(8);

    // Five case-known messages of the linked set inside five seconds, the first and the last two seconds apart: all three
    // are design choices, reused from the link rule (BroadcastRules.LinkMessages, LinkWindow, LinkSpan). Records for the
    // margin: a real open ran at 4.2 messages a second (110 messages in 26.1 s, 30 September) and 8 to 124 linked messages
    // came in the first minute of each link; the false events are one or two. The window is half open: a message exactly
    // OpenWindow old no longer counts.
    public const int OpenMessages = BroadcastRules.LinkMessages;

    public static readonly TimeSpan OpenWindow = BroadcastRules.LinkWindow;

    public static readonly TimeSpan OpenSpan = BroadcastRules.LinkSpan;

    // How many signal levels one episode of messages keeps for its median, so a long slow trickle cannot grow without bound.
    private const int MaxEpisodeLevels = 64;

    private readonly record struct Counted(DateTimeOffset At, uint Sender, sbyte Rssi);

    // The case-known messages of the last OpenWindow while closed.
    private readonly List<Counted> _burst = new();

    // The run of case-known messages since the last gap of OpenWindow while closed, for the line a burst that never opened
    // writes. Counts only; the levels are capped.
    private int _episodeMessages;
    private DateTimeOffset _episodeFirst;
    private DateTimeOffset _episodeLast;
    private readonly HashSet<uint> _episodeSenders = new();
    private readonly List<double> _episodeLevels = new();
    private byte _episodeLid;

    private DateTimeOffset? _lastCaseKnownAt;
    private DateTimeOffset? _openedAt;
    private DateTimeOffset? _lastClosedAt;
    private int _openMessages;
    private byte _openLid;
    private byte _lastLid;

    private readonly List<string> _lines = new();

    // True from an open until the close.
    public bool IsOpen => _openedAt is not null;

    // When the case counts as closed if no other case-known message comes: the last one plus CloseAfter. Null when closed.
    public DateTimeOffset? CloseDueAt => IsOpen && _lastCaseKnownAt is { } last ? last + CloseAfter : null;

    // When Expire has something to do: the close time while open; while closed, the moment the burst under way lapses
    // without an open (its last message plus OpenWindow), so the line that says so is written when it happens and not at
    // the next message, which may be hours away. Null when there is nothing due.
    public DateTimeOffset? NextDueAt => IsOpen ? CloseDueAt : _episodeMessages > 0 ? _episodeLast + OpenWindow : null;

    // One message of the linked set. caseKnown: its case nibble is a level. lidByte: the message's lid byte, logged and
    // never read. rssi: its signal level in dBm. linkMade: this message made the link (or moved it to this set).
    public CaseOpenStep Observe(uint sender, bool caseKnown, byte lidByte, sbyte rssi, DateTimeOffset at, bool linkMade)
    {
        if (!caseKnown)
        {
            return CaseOpenStep.None;
        }

        // A late clock: the case closed before this message, and a burst under way may have lapsed, whatever the timer has
        // done yet.
        Expire(at);

        _lastLid = lidByte;
        if (IsOpen)
        {
            _lastCaseKnownAt = at;
            _openMessages++;
            return CaseOpenStep.None;
        }

        if (linkMade)
        {
            Open(at, lidByte, byLink: true, first: at, count: 1, span: TimeSpan.Zero, senders: 1, median: rssi);
            return CaseOpenStep.Opened;
        }

        _burst.Add(new Counted(at, sender, rssi));
        _burst.RemoveAll(m => at - m.At >= OpenWindow);
        NoteEpisode(sender, rssi, at, lidByte);

        DateTimeOffset first = _burst[0].At;
        if (_burst.Count >= OpenMessages && at - first >= OpenSpan)
        {
            double median = Median(_burst.Select(m => (double)m.Rssi).ToList());
            Open(at, lidByte, byLink: false, first, _burst.Count, at - first, _burst.Select(m => m.Sender).Distinct().Count(), median);
            return CaseOpenStep.Opened;
        }

        return CaseOpenStep.None;
    }

    // Closes the case when no case-known message has come for CloseAfter by now, and writes the line for a burst that has
    // lapsed without opening. True when this call closed the case.
    public bool Expire(DateTimeOffset now)
    {
        if (CloseDueAt is { } due && now >= due)
        {
            WriteClosed();
            _lastClosedAt = due;
            _openedAt = null;
            _openMessages = 0;
            return true;
        }

        if (!IsOpen && _episodeMessages > 0 && now - _episodeLast >= OpenWindow)
        {
            WriteMiss();
            ClearBurst();
        }

        return false;
    }

    // A different pair was linked, or the link was dropped: nothing of the old pair's case carries over. True when the case
    // was open.
    public bool Reset()
    {
        bool wasOpen = IsOpen;
        _openedAt = null;
        _lastCaseKnownAt = null;
        _lastClosedAt = null;
        _openMessages = 0;
        ClearBurst();
        return wasOpen;
    }

    // The lines written since the last call, oldest first, for the log. Each is one event; none has an address.
    public IReadOnlyList<string> TakeLines()
    {
        if (_lines.Count == 0)
        {
            return Array.Empty<string>();
        }

        string[] taken = _lines.ToArray();
        _lines.Clear();
        return taken;
    }

    private void Open(DateTimeOffset at, byte lid, bool byLink, DateTimeOffset first, int count, TimeSpan span, int senders, double median)
    {
        string since = SinceLastClose(first);
        _lines.Add(byLink
            ? "Case: open by link: lid " + Hex(lid) + "."
            : "Case: open by burst: " + Event(count, span, senders, median, lid, since));
        _openedAt = at;
        _lastCaseKnownAt = at;
        _openMessages = count;
        _openLid = lid;
        ClearBurst();
    }

    private void WriteClosed()
    {
        DateTimeOffset opened = _openedAt ?? _lastCaseKnownAt ?? default;
        TimeSpan openFor = (_lastCaseKnownAt ?? opened) - opened;
        _lines.Add(
            "Case: closed after " + CloseAfter.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s of silence: open for " +
            Ms(openFor) + " ms, " + _openMessages.ToString(CultureInfo.InvariantCulture) + " messages, lid " + Hex(_openLid) + " to " +
            Hex(_lastLid) + ".");
    }

    private void WriteMiss()
    {
        _lines.Add(
            "Case: not an open: " + Event(
                _episodeMessages, _episodeLast - _episodeFirst, _episodeSenders.Count, Median(_episodeLevels), _episodeLid,
                SinceLastClose(_episodeFirst)));
    }

    private static string Event(int count, TimeSpan span, int senders, double median, byte lid, string since) =>
        count.ToString(CultureInfo.InvariantCulture) + " messages in " + Ms(span) + " ms from " +
        senders.ToString(CultureInfo.InvariantCulture) + (senders == 1 ? " sender" : " senders") + ", median " +
        median.ToString("0.#", CultureInfo.InvariantCulture) + " dBm, lid " + Hex(lid) + ", " + since;

    private string SinceLastClose(DateTimeOffset at) =>
        _lastClosedAt is { } closed
            ? Ms(at - closed) + " ms since the last close."
            : "no close before it.";

    private void NoteEpisode(uint sender, sbyte rssi, DateTimeOffset at, byte lid)
    {
        if (_episodeMessages == 0)
        {
            _episodeFirst = at;
        }

        _episodeMessages++;
        _episodeLast = at;
        _episodeLid = lid;
        _episodeSenders.Add(sender);
        if (_episodeLevels.Count < MaxEpisodeLevels)
        {
            _episodeLevels.Add(rssi);
        }
    }

    private void ClearBurst()
    {
        _burst.Clear();
        ResetEpisode();
    }

    private void ResetEpisode()
    {
        _episodeMessages = 0;
        _episodeSenders.Clear();
        _episodeLevels.Clear();
    }

    private static string Ms(TimeSpan span) =>
        ((long)Math.Round(span.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture);

    private static string Hex(byte value) => "0x" + value.ToString("X2", CultureInfo.InvariantCulture);

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        double[] sorted = values.Order().ToArray();
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
