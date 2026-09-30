using Earshot.Contracts;

namespace Earshot.Widget;

// One advertisement a set-up window heard, kept until the window ends. Sequence is the order the messages arrived
// in, which is what "newest" means when two senders' timestamps are equal.
internal sealed class SetupSample(
    long sequence, uint senderTag, DateTimeOffset at, sbyte rssi, ProximityParseStatus status,
    byte? prefix, int length, string valueHex, ProximityMessage? message)
{
    public long Sequence { get; } = sequence;

    public uint SenderTag { get; } = senderTag;

    public DateTimeOffset At { get; } = at;

    public sbyte Rssi { get; } = rssi;

    public ProximityParseStatus Status { get; } = status;

    public byte? Prefix { get; } = prefix;

    public int Length { get; } = length;

    public string ValueHex { get; } = valueHex;

    public ProximityMessage? Message { get; } = message;
}

// The documented-form senders that are one set of AirPods. Tags are in the order each sender was first heard,
// Samples in arrival order.
internal sealed class SetupSenderGroup(IReadOnlyList<uint> tags, IReadOnlyList<SetupSample> samples)
{
    public IReadOnlyList<uint> Tags { get; } = tags;

    public IReadOnlyList<SetupSample> Samples { get; } = samples;
}

// Each bud of one set advertises on its own, so one set is two senders. Two senders are one set when they share a
// model and a colour and, within SetupRules.SameSetWithin of each other, sent a message with the same case level and
// the same two bud levels as an unordered pair (each bud reports itself first, so the pair arrives in opposite order).
// The relation is closed transitively, so a third sender that matches either of the first two joins them.
internal static class SetupSenderGroups
{
    public static List<SetupSenderGroup> Merge(IReadOnlyList<(uint Tag, List<SetupSample> Samples)> senders)
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
                if (Find(i) != Find(j) && AreOneSet(senders[i].Samples, senders[j].Samples))
                {
                    parent[Find(j)] = Find(i);
                }
            }
        }

        var groups = new List<SetupSenderGroup>();
        foreach (IGrouping<int, int> members in Enumerable.Range(0, count).GroupBy(Find))
        {
            // Senders are already ordered by when each was first heard, and GroupBy keeps that order.
            var tags = members.Select(i => senders[i].Tag).ToList();
            var samples = members.SelectMany(i => senders[i].Samples).OrderBy(s => s.Sequence).ToList();
            groups.Add(new SetupSenderGroup(tags, samples));
        }

        return groups;
    }

    private static bool AreOneSet(List<SetupSample> first, List<SetupSample> second)
    {
        foreach (SetupSample a in first)
        {
            foreach (SetupSample b in second)
            {
                if (SameSet(a, b))
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static bool SameSet(SetupSample a, SetupSample b)
    {
        if (a.Message is not ProximityMessage x || b.Message is not ProximityMessage y)
        {
            return false;
        }

        if (x.ModelHigh != y.ModelHigh || x.ModelLow != y.ModelLow || x.Colour != y.Colour)
        {
            return false;
        }

        if ((x.BatteryB & 0x0F) != (y.BatteryB & 0x0F) || !SameBudPair(x.BatteryA, y.BatteryA))
        {
            return false;
        }

        return (a.At - b.At).Duration() <= SetupRules.SameSetWithin;
    }

    // The two bud nibbles as an unordered pair.
    private static bool SameBudPair(byte first, byte second)
    {
        int firstHigh = (first >> 4) & 0x0F;
        int firstLow = first & 0x0F;
        int secondHigh = (second >> 4) & 0x0F;
        int secondLow = second & 0x0F;
        return (firstHigh == secondHigh && firstLow == secondLow) || (firstHigh == secondLow && firstLow == secondHigh);
    }
}
