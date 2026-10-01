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

    // The paired model has to have been heard this long before the first choice, so a second set nearby can
    // show up before the first one is taken. The set with the case open sends about four messages a second.
    public static readonly TimeSpan FirstChoiceAfter = TimeSpan.FromSeconds(2);

    // Two senders are one set when they sent the same case level and the same two bud levels (as an unordered
    // pair) this close together, with the same model and colour. Each bud advertises on its own, so the two buds
    // of one set land within a second or so of each other whenever both are sending; two seconds is that gap
    // with room for a missed message.
    public static readonly TimeSpan SameSetWithin = TimeSpan.FromSeconds(2);

    // A sender that merely merged into the chosen set (it said what an anchor said, near it in time) becomes an anchor
    // only after it has matched anchor messages this many times, the first of the run this long before the last. A
    // passer-by whose levels happen to equal the owner's is heard once or twice before the next of its messages differs;
    // the owner's other bud matches every message it sends. Three matches is the count BroadcastRules.MinMessages
    // already calls a median rather than a passer-by, and three seconds is the shortest run in which the case-open rate
    // (about four messages a second) gives that many while the in-use rate (a message every few seconds per bud) needs
    // longer, which is harmless: the first sender is an anchor already, and a second bud that is not one yet only means
    // the set is chosen again, by the first-choice rule, if the first goes quiet for longer than the window. Both are
    // design choices, not measurements: no second set was ever near the owner.
    public const int AnchorMatches = 3;

    public static readonly TimeSpan AnchorSpan = TimeSpan.FromSeconds(3);

    // Another set takes over only when its median is at least this far above the chosen set's, for the whole
    // of SwitchHold. 8 dB is above the largest wander of a set that did not move (6.5 dB), so the set that is
    // not nearer does not cross it on noise in one window; 30 s is three back-to-back windows, and in the one
    // capture long enough to check, the 20 s median did not move at all. Someone walking away with one pair
    // while another stays near is followed within 30 s. Only one set's noise and synthetic sequences have been
    // checked: no second set was ever near the owner.
    public const double SwitchMarginDb = 8;

    public static readonly TimeSpan SwitchHold = TimeSpan.FromSeconds(30);

    // The chosen set and the held colour are dropped after this long with no message from it (the same hour
    // after which the gauge drops a value), so a wrong first choice cannot lock the owner's pair out for ever.
    public static readonly TimeSpan ReleaseAfter = TimeSpan.FromHours(1);

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

    private static bool SameBudPair(byte first, byte second)
    {
        int firstHigh = (first >> 4) & 0x0F;
        int firstLow = first & 0x0F;
        int secondHigh = (second >> 4) & 0x0F;
        int secondLow = second & 0x0F;
        return (firstHigh == secondHigh && firstLow == secondLow) || (firstHigh == secondLow && firstLow == secondHigh);
    }
}
