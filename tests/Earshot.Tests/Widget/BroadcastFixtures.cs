using Earshot.Widget;

namespace Earshot.Tests.Widget;

// Synthetic messages of one invented set of AirPods for the selection tests: the model and colour are
// WidgetFixtures' own "no product" bytes, never a real one.
internal static class BroadcastFixtures
{
    public static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    // The paired AirPods' model as the selector is given it: WidgetFixtures' two model bytes, first on the wire
    // the low byte.
    public const ushort PairedModel = WidgetFixtures.ModelHigh | (WidgetFixtures.ModelLow << 8);

    // One bud's message of a pair. Each bud reports its own level first, so the two bud nibbles arrive in opposite
    // order and bit 5 of the status byte differs; the case level is shared and no charging bit is set.
    public static ProximityMessage Bud(
        bool first, byte colour = WidgetFixtures.Colour, byte caseNibble = 0x9, int pairHigh = 0x7, int pairLow = 0x4,
        byte modelHigh = WidgetFixtures.ModelHigh, byte modelLow = WidgetFixtures.ModelLow)
    {
        byte batteryA = first ? (byte)((pairHigh << 4) | pairLow) : (byte)((pairLow << 4) | pairHigh);
        byte status = first ? (byte)0x40 : (byte)0x60;
        return new ProximityMessage(modelHigh, modelLow, status, batteryA, caseNibble, 0x00, colour, 0x00);
    }

    // A different set: other bud levels, so it cannot be mistaken for the one above.
    public static ProximityMessage OtherSet(byte colour = WidgetFixtures.Colour, byte caseNibble = 0x2) =>
        Bud(first: true, colour, caseNibble, pairHigh: 0x3, pairLow: 0x1);

    public static ProximityMessage Raw(byte status = 0x00, byte batteryA = 0x00, byte batteryB = 0x00, byte colour = WidgetFixtures.Colour) =>
        new(WidgetFixtures.ModelHigh, WidgetFixtures.ModelLow, status, batteryA, batteryB, 0x00, colour, 0x00);
}

// Feeds a selector messages with the clock the messages carry, so a test reads as a timeline.
internal sealed class SelectorDriver
{
    public SelectorDriver(ushort? model = BroadcastFixtures.PairedModel)
    {
        Selector = new BroadcastSelector();
        Selector.SetPairedModel(model);
    }

    public BroadcastSelector Selector { get; }

    public DateTimeOffset Now { get; private set; } = BroadcastFixtures.Start;

    public void Tick(double seconds) => Now += TimeSpan.FromSeconds(seconds);

    public SelectionObservation Send(uint tag, ProximityMessage message, sbyte rssi) => Selector.Observe(message, tag, rssi, Now);

    // One sender sending the same message every interval seconds for seconds seconds (both ends inclusive).
    // Returns the last observation.
    public SelectionObservation Run(uint tag, ProximityMessage message, sbyte rssi, double seconds, double interval = 0.5)
    {
        SelectionObservation last = default;
        int steps = (int)Math.Round(seconds / interval);
        for (int i = 0; i <= steps; i++)
        {
            last = Send(tag, message, rssi);
            if (i < steps)
            {
                Tick(interval);
            }
        }

        return last;
    }
}
