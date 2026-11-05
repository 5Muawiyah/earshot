using Earshot.Bluetooth;
using Earshot.Widget;

namespace Earshot.App;

// Kept out of Earshot.Widget, which holds no Bluetooth type.
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
