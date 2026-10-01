namespace Earshot.Widget;

// The figures the selection of the owner's AirPods decides with. Each is a design choice, kept as a named
// constant, backed by how much one stationary set's signal wanders in the saved captures; none is a fact about
// the device.
internal static class BroadcastRules
{
    // The window every set's signal and message count is taken over. At the slowest in-use rate seen (about
    // 35 messages a minute for a set) it holds about six messages, enough for a median, and its median wanders
    // 3 to 6.5 dB for a set that did not move, well under a 5 s window's 7 to 10 dB.
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    // Fewer messages than this in the window is a passer-by, not a median.
    public const int MinMessages = 3;

    // ---- Linking on a case open
    //
    // The owner's pair is linked when the owner opens the case next to the PC. A pair in a case with the lid open sends
    // a message of the paired model with the case level known (the case nibble is a level, not 0xF) about four times a
    // second between its two buds; a pair in use sends one with the case level unknown, once every second or two. The
    // case level is what tells the owner opening the case from every pair worn nearby.
    //
    // The near-PC level. The owner's pair with both buds in the case and the lid open was recorded three times (the
    // 30 September capture and two set-up records of 1 October: about 300 messages, about four a second). The median
    // over each window of five consecutive messages of the set ran from -72 to -51 dBm: the weakest window of all was
    // -72, the middle window of each record was -61 to -58, and 95% of the weakest record's windows were above -70. A
    // pair worn nearby in the evening of the same day, which was shown as the owner's, read -64 to -76 dBm with the case
    // level unknown. So no level separates the owner from a same-model stranger who opens a case at the same distance;
    // the level only keeps out a pair that is not next to the PC. -70 dBm is above all but a few windows of the weakest
    // record (a link then waits a moment for a stronger window) and well below what the owner's pair usually read.
    public const double LinkThresholdDbm = -70;

    // Five case-known messages of one set inside LinkWindow, the first and the last at least LinkSpan apart: a burst of
    // about two seconds at the case-open rate (nine messages), which one stray message, or the few a pair sends in
    // passing as a bud leaves the case, cannot make. The window is a little over twice that burst. Of sets that qualify at
    // the same message the strongest is linked; a set qualifies on its own message, so in practice the first to qualify is
    // linked, and a set that opens its case later but SwitchMarginDb stronger takes the link.
    public const int LinkMessages = 5;

    public static readonly TimeSpan LinkWindow = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan LinkSpan = TimeSpan.FromSeconds(2);

    // A linked set that is not heard under any of its addresses is followed to a set that continues its last fields
    // (Continues: the same model, colour and two bud levels, and the same case level unless either message gives none, since
    // the case level comes and goes as the buds leave the case and return) only within this long of its last message, the
    // freshness window of a value (BatteryFreshness.FreshWindow). After that the new address is not taken to be the old,
    // whatever it says.
    public static readonly TimeSpan ContinueWithin = TimeSpan.FromSeconds(30);

    // The set that continues has to have been heard this long, with MinMessages in the window, so one passing message
    // equal to the linked set's is not followed.
    public static readonly TimeSpan ContinueAfter = TimeSpan.FromSeconds(2);

    // The link is dropped, and nothing is shown, when the linked set has not been heard under any address for this
    // long. An address change takes seconds (the longest silence of a set in any record is a few seconds; the addresses
    // change at a time the saved records do not show, so this figure is a margin, not a measurement), so two minutes
    // covers one and a pair put back in the case for a moment. It does not cover a session: a pair left alone for
    // longer than this is somebody else's by the time it is heard again, and the next case open links it.
    public static readonly TimeSpan LostLimit = TimeSpan.FromMinutes(2);

    // Two senders are one set when they sent the same case level and the same two bud levels (as an unordered
    // pair) this close together, with the same model and colour. Each bud advertises on its own, so the two buds
    // of one set land within a second or so of each other whenever both are sending; two seconds is that gap
    // with room for a missed message.
    public static readonly TimeSpan SameSetWithin = TimeSpan.FromSeconds(2);

    // A sender that merely merged into the linked set (it said what an anchor said, near it in time) becomes an anchor
    // only after it has matched anchor messages this many times, the first of the run this long before the last. The
    // design assumes that a passer-by whose levels happen to equal the owner's stops matching after a message or two,
    // while the owner's other bud matches every message it sends. That is an assumption, not something that was measured:
    // a same-model stranger whose levels stay equal to the owner's for AnchorSpan can become an anchor, which is part of
    // the same-model risk the documentation states (an equal or lower battery level of the same model can pass as the
    // owner's). Three matches is the count BroadcastRules.MinMessages
    // already calls a median rather than a passer-by, and three seconds is the shortest run in which the case-open rate
    // (about four messages a second) gives that many while the in-use rate (a message every few seconds per bud) needs
    // longer, which is harmless: the first sender is an anchor already, and a second bud that is not one yet only means
    // the set is followed by its fields, if the first goes quiet for longer than the window. Both are design choices,
    // not measurements: no second set was ever near the owner.
    public const int AnchorMatches = 3;

    public static readonly TimeSpan AnchorSpan = TimeSpan.FromSeconds(3);

    // Another set that opens its case takes the link from the linked one only when its median is at least this far above
    // the linked set's. 8 dB is above the largest wander of a set that did not move (6.5 dB, over the 10 s windows of the
    // saved records), so a pair that is not nearer does not cross it on noise. It is how the owner corrects a link made
    // to somebody else's pair: open the case next to the PC. Only one set's noise and synthetic sequences have been
    // checked: no second set was ever near the owner.
    public const double SwitchMarginDb = 8;

    // Bounds, so a busy neighbourhood cannot grow the selector without limit.
    public const int MaxSenders = 64;

    public const int MaxMessagesPerSender = 200;
}

// One documented-form message the selector kept. Sequence is the order the messages arrived in, which is what
// "newest" means when two timestamps are equal.
internal sealed class SenderMessage(long sequence, uint tag, DateTimeOffset at, sbyte rssi, ProximityMessage message)
{
    public long Sequence { get; } = sequence;

    public uint Tag { get; } = tag;

    public DateTimeOffset At { get; } = at;

    public sbyte Rssi { get; } = rssi;

    public ProximityMessage Message { get; } = message;
}

// The senders that are one set of AirPods, with the messages each sent inside the window. Tags are in the order
// each sender was first heard, Messages in arrival order.
internal sealed class BroadcastSet(IReadOnlyList<uint> tags, IReadOnlyList<SenderMessage> messages)
{
    public IReadOnlyList<uint> Tags { get; } = tags;

    public IReadOnlyList<SenderMessage> Messages { get; } = messages;

    public SenderMessage Newest => Messages[^1];

    // The median signal over the window, or null with fewer than BroadcastRules.MinMessages messages.
    public double? MedianRssi
    {
        get
        {
            if (Messages.Count < BroadcastRules.MinMessages)
            {
                return null;
            }

            double[] sorted = Messages.Select(m => (double)m.Rssi).Order().ToArray();
            int mid = sorted.Length / 2;
            return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
        }
    }
}

// Each bud of one set advertises on its own, so one set is two senders. They are told apart from two sets by
// what they say: the same model and colour, the same case level and the same two bud levels as an unordered
// pair (each bud reports itself first, so the pair arrives in opposite order), within SameSetWithin. The
// relation is closed transitively, so a third sender that matches either of the first two joins them. A set of
// the same model, colour and levels heard in the same two seconds cannot be told from the owner's own, which is
// the risk the owner accepted for a same-model pair nearby.
internal static class BroadcastSenderSets
{
    public static List<BroadcastSet> Compute(IReadOnlyList<(uint Tag, List<SenderMessage> Messages)> senders)
    {
        ArgumentNullException.ThrowIfNull(senders);

        int count = senders.Count;
        int[] parent = new int[count];
        for (int i = 0; i < count; i++)
        {
            parent[i] = i;
        }

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        for (int i = 0; i < count; i++)
        {
            for (int j = i + 1; j < count; j++)
            {
                if (Find(i) != Find(j) && AreOneSet(senders[i].Messages, senders[j].Messages))
                {
                    parent[Find(j)] = Find(i);
                }
            }
        }

        var sets = new List<BroadcastSet>();
        foreach (IGrouping<int, int> members in Enumerable.Range(0, count).GroupBy(Find))
        {
            // Senders are already ordered by when each was first heard, and GroupBy keeps that order.
            var tags = members.Select(i => senders[i].Tag).ToList();
            var messages = members.SelectMany(i => senders[i].Messages).OrderBy(m => m.Sequence).ToList();
            if (messages.Count > 0)
            {
                sets.Add(new BroadcastSet(tags, messages));
            }
        }

        return sets;
    }

    private static bool AreOneSet(List<SenderMessage> first, List<SenderMessage> second)
    {
        foreach (SenderMessage a in first)
        {
            foreach (SenderMessage b in second)
            {
                if (SameSet(a, b))
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static bool SameSet(SenderMessage a, SenderMessage b) =>
        SameFields(a.Message, b.Message) && (a.At - b.At).Duration() <= BroadcastRules.SameSetWithin;

    // The same model and colour and the same battery fields: the case level and the two bud levels as an
    // unordered pair.
    internal static bool SameFields(ProximityMessage x, ProximityMessage y) =>
        x.Model == y.Model &&
        x.Colour == y.Colour &&
        (x.BatteryB & 0x0F) == (y.BatteryB & 0x0F) &&
        SameBudPair(x.BatteryA, y.BatteryA);

    // Whether a message continues what a linked set last said: the same model and colour and the same two bud levels as
    // an unordered pair, and the same case level unless either message gives none. A pair's case level is sent only while
    // a bud is in the case with the lid open, so the same set legitimately goes from a level to none (the buds were taken
    // out) or back, whatever address it sends from; the bud levels and the colour are what stay.
    internal static bool Continues(ProximityMessage last, ProximityMessage next) =>
        last.Model == next.Model &&
        last.Colour == next.Colour &&
        SameBudPair(last.BatteryA, next.BatteryA) &&
        ((last.BatteryB & 0x0F) == (next.BatteryB & 0x0F) || !CaseKnown(last) || !CaseKnown(next));

    // A message whose case nibble is a level. 0xF, and 11 to 14, are not.
    internal static bool CaseKnown(ProximityMessage message) => BatteryNibble.ToPercent(message.BatteryB & 0x0F) is not null;

    private static bool SameBudPair(byte first, byte second)
    {
        int firstHigh = (first >> 4) & 0x0F;
        int firstLow = first & 0x0F;
        int secondHigh = (second >> 4) & 0x0F;
        int secondLow = second & 0x0F;
        return (firstHigh == secondHigh && firstLow == secondLow) || (firstHigh == secondLow && firstLow == secondHigh);
    }
}
