using Earshot.Bluetooth;

namespace Earshot.Widget;

// What the card knows of this PC's own Bluetooth, from the radio check's last result: Ok says nothing is wrong, Off is a radio that is
// off or switched off, NotWorking a radio that is absent, cannot do Classic Bluetooth or reports a problem, and NoAirPodsPaired a PC
// with no AirPods paired. The card shows them only where it opened as asked.
internal enum CardRadio { Ok, Off, NotWorking, NoAirPodsPaired }

internal static class CardRadioMap
{
    // The state a radio check result stands for, or null when the result says nothing about the radio (Unreachable is about a
    // pairing, Unknown is a read that did not answer).
    public static CardRadio? FromOrNull(RadioCheckState state) => state switch
    {
        RadioCheckState.Ok => CardRadio.Ok,
        RadioCheckState.Off or RadioCheckState.SwitchedOff => CardRadio.Off,
        RadioCheckState.Problem or RadioCheckState.NoAdapter or RadioCheckState.NoClassic => CardRadio.NotWorking,
        _ => null,
    };

    public static CardRadio From(RadioCheckState state) => FromOrNull(state) ?? CardRadio.Ok;
}
