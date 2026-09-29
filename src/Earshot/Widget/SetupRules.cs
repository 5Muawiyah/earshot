namespace Earshot.Widget;

// The three figures the battery set-up decides with. Each is a design choice, kept as a named constant so it
// can be revisited from the evidence a set-up record keeps; none is a fact about the device.
public static class SetupRules
{
    // One message is a passer-by. The documented form repeats several times a second with the lid open, so
    // three within the listening window is a low bar for the owner's own case and a real one for a stranger
    // walking past.
    public const int MinMessages = 3;

    // How far the strongest sender's median must sit above the next one's. The case is on the desk a hand's
    // width from the radio and its signal moves a few decibels with orientation and a hand near it, so a
    // second sender inside ten decibels cannot be told from it.
    public const int SeparationDb = 10;

    // The threshold sits this far under the weakest message the owner's own case sent, so the same case in
    // the same place stays readable and anything much further away is rejected.
    public const int SignalMarginDb = 10;
}
