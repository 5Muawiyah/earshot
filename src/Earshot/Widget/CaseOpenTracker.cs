namespace Earshot.Widget;

// What happened to the linked pair's case at one message, for the case-open card.
internal enum CaseOpenStep { None, Opened }

// Whether the linked pair's case is open, worked out from the linked pair's own messages only. Pure: the time is handed in,
// and WidgetStatusService feeds it every message of the linked set (and of no other) under its lock.
//
// An open is either of two things.
//  - The linked pair's case-known messages starting after silence: a pair in its case with the lid open sends the case level
//    about four times a second between its two buds; a shut case sends nothing, and a pair in use sends the case level as
//    unknown (0xF). So a case-known message after no case-known message of the linked pair for CloseAfter (or none since the
//    link was made) is the lid being opened. The link itself is made from case-known messages (BroadcastRules), so the
//    message that links a pair is such an open too.
//  - The lid open counter changing while the case-known messages never stopped: the case shut and opened again inside
//    CloseAfter. The counter is the one-byte "Lid Open Counter" of the furiousMAC notes (ProximityDecodeTable.Documented).
//    Each sender's counter is compared only with that sender's own last value, since nothing says the two buds of a pair
//    carry the same value; the first value from a sender is only a baseline.
// A case-unknown message never opens anything, so buds being worn never show the card, and a closed case sends nothing.
//
// A close is the linked pair's case-known messages stopping for CloseAfter. No source documents a bit that says the lid is
// shut (only the counter of opens), and the in-ear bits are documented by one note only, so the cadence is what closes it.
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

    // A counter change within this long of an open is the same open seen from the pair's other bud, not another: the two buds
    // may report one open about a message apart. The set-membership window (BroadcastRules.SameSetWithin), reused for the
    // same reason; a design choice.
    public static readonly TimeSpan SameOpenWithin = BroadcastRules.SameSetWithin;

    private readonly Dictionary<uint, int> _counterBySender = new();

    // The senders whose counter has not yet caught up with a reopen another sender showed first. One reopen moves every bud's
    // counter, but one sender's gap between messages (6.14 s at the longest in the saved records) can be longer than
    // SameOpenWithin, so the other bud's change can arrive after it. Its first change inside CloseAfter of that open is that
    // reopen seen late, not another one; a design choice, with the cost that a second reopen inside CloseAfter that only the
    // lagging bud reports is not counted.
    private readonly HashSet<uint> _catchingUp = new();
    private DateTimeOffset? _lastCaseKnownAt;
    private DateTimeOffset? _openedAt;

    // True from an open until the close.
    public bool IsOpen => _openedAt is not null;

    // When the case counts as closed if no other case-known message comes: the last one plus CloseAfter. Null when closed.
    public DateTimeOffset? CloseDueAt => IsOpen && _lastCaseKnownAt is { } last ? last + CloseAfter : null;

    // One message of the linked set. caseKnown: its case nibble is a level. lidCounter: the decoded counter, or null when the
    // table reads none.
    public CaseOpenStep Observe(uint sender, bool caseKnown, int? lidCounter, DateTimeOffset at)
    {
        if (!caseKnown)
        {
            return CaseOpenStep.None;
        }

        // A late clock: the case closed before this message, whatever the timer has done yet.
        Expire(at);

        bool counterMoved = lidCounter is int counter && _counterBySender.TryGetValue(sender, out int last) && last != counter;
        if (lidCounter is int value)
        {
            _counterBySender[sender] = value;
        }

        _lastCaseKnownAt = at;
        if (!IsOpen)
        {
            _openedAt = at;
            _catchingUp.Clear();
            return CaseOpenStep.Opened;
        }

        if (counterMoved && _openedAt is { } opened)
        {
            if (_catchingUp.Remove(sender) && at - opened <= CloseAfter)
            {
                return CaseOpenStep.None;
            }

            if (at - opened > SameOpenWithin)
            {
                _openedAt = at;
                _catchingUp.Clear();
                foreach (uint other in _counterBySender.Keys)
                {
                    if (other != sender)
                    {
                        _catchingUp.Add(other);
                    }
                }

                return CaseOpenStep.Opened;
            }
        }

        return CaseOpenStep.None;
    }

    // Closes the case when no case-known message has come for CloseAfter by now. True when this call closed it.
    public bool Expire(DateTimeOffset now)
    {
        if (CloseDueAt is { } due && now >= due)
        {
            _openedAt = null;
            _counterBySender.Clear();
            _catchingUp.Clear();
            return true;
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
        _counterBySender.Clear();
        _catchingUp.Clear();
        return wasOpen;
    }
}
