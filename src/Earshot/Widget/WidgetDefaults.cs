namespace Earshot.Widget;

// Facts the phase 0 sitting has to prove on the owner's own AirPods. Both ship null: nothing here is ever
// a guess, and every feature that depends on one fails closed until phase 0 has filled it in.
public static class WidgetDefaults
{
    // The RSSI, in dBm, at or above which the phase 0 sitting found the owner's own sender readable with
    // his case next to the PC. Null until then; a claim cannot be made while it is null.
    public static readonly sbyte? SignalThresholdDbm;

    // Whether the owner's AirPods keep broadcasting while worn and playing from this PC. Null until phase 0
    // step 6 records it; auto-pause never acts while it is not true.
    public static readonly bool? BroadcastContinuesWhilePlayingFromThisPc;
}
