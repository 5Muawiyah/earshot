namespace Earshot.Widget;

// What the card shows of a battery refresh: the icon turns while Reading (SpinFrame, 0 to 9, a tenth of a turn
// each), and once it has ended without fresh values the read line says why (Outcome). A refresh that heard a
// message or read Windows' figure needs no words: the values on the card are the answer.
internal sealed record BatteryRefreshView(bool Reading, int SpinFrame = 0, BatteryRefreshOutcome? Outcome = null)
{
    public const int SpinFrames = 10;

    public static BatteryRefreshView Started { get; } = new(Reading: true);

    // The read line while a refresh is showing something, or null when the ordinary read line stands.
    public string? ReadLine =>
        Reading ? WidgetCopy.ReadingBattery
        : Outcome switch
        {
            BatteryRefreshOutcome.NothingHeard => WidgetCopy.NothingHeardOpenTheCase,
            BatteryRefreshOutcome.BluetoothOff => WidgetCopy.BluetoothIsOff,
            BatteryRefreshOutcome.NotListening => WidgetCopy.NotListening,
            _ => null,
        };
}
