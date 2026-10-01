namespace Earshot.Widget;

// How the gauge's three pieces line up, left to right: the ring (with the earbud mark inside it), the number and the
// charging bolt. Stored in the settings as its number; a number that names none of them is read as the first, which
// is the layout the gauge has always had.
public enum GaugeOrder
{
    RingNumberBolt = 0,
    RingBoltNumber = 1,
    NumberRingBolt = 2,
    NumberBoltRing = 3,
    BoltRingNumber = 4,
    BoltNumberRing = 5,
}

// The three pieces of the gauge.
internal enum GaugePiece { Ring, Number, Bolt }

internal static class GaugeOrders
{
    // The pieces of an order, left to right.
    public static (GaugePiece First, GaugePiece Second, GaugePiece Third) Sequence(GaugeOrder order) => order switch
    {
        GaugeOrder.RingNumberBolt => (GaugePiece.Ring, GaugePiece.Number, GaugePiece.Bolt),
        GaugeOrder.RingBoltNumber => (GaugePiece.Ring, GaugePiece.Bolt, GaugePiece.Number),
        GaugeOrder.NumberRingBolt => (GaugePiece.Number, GaugePiece.Ring, GaugePiece.Bolt),
        GaugeOrder.NumberBoltRing => (GaugePiece.Number, GaugePiece.Bolt, GaugePiece.Ring),
        GaugeOrder.BoltRingNumber => (GaugePiece.Bolt, GaugePiece.Ring, GaugePiece.Number),
        GaugeOrder.BoltNumberRing => (GaugePiece.Bolt, GaugePiece.Number, GaugePiece.Ring),
        _ => (GaugePiece.Ring, GaugePiece.Number, GaugePiece.Bolt),
    };

    // The words a screen reader says for a picture of an order. They are for assistive technology only; the picker
    // shows the gauge itself.
    public static string AccessibleName(GaugeOrder order) => order switch
    {
        GaugeOrder.RingNumberBolt => "Ring, number, bolt",
        GaugeOrder.RingBoltNumber => "Ring, bolt, number",
        GaugeOrder.NumberRingBolt => "Number, ring, bolt",
        GaugeOrder.NumberBoltRing => "Number, bolt, ring",
        GaugeOrder.BoltRingNumber => "Bolt, ring, number",
        GaugeOrder.BoltNumberRing => "Bolt, number, ring",
        _ => "Ring, number, bolt",
    };

    // The order for a stored number: the first for one that names none.
    public static GaugeOrder FromStored(GaugeOrder stored) => Enum.IsDefined(stored) ? stored : GaugeOrder.RingNumberBolt;
}
