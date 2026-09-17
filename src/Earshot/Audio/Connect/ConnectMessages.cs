namespace Earshot.Audio.Connect;

// Every message a connect or disconnect can end with. British English, short, no em-dashes. The
// controller never reports Connected or Disconnected from an HRESULT alone: those two follow an observed
// endpoint state only.
internal static class ConnectMessages
{
    // The render endpoint reached ACTIVE in time.
    public const string Connected = "Connected";

    // The render endpoint left ACTIVE in time.
    public const string Disconnected = "Disconnected";

    // A request was accepted but no state change came in time (connect).
    public const string StillConnecting = "Still connecting. Check your AirPods.";

    // No filter accepted the request. The block coordinator owns the other way (Allow then reconnect, or
    // Block), so it replaces this message when it does not take one.
    public const string CouldNotReachDriver = "Could not reach the AirPods audio driver. Trying another way.";

    // Connect, and every render endpoint is NOTPRESENT. The block coordinator allows the device nodes first,
    // then connects, and replaces this message when it does not.
    public const string AllowingFirst = "Allowing first, then connecting.";

    // The audio endpoints could not be enumerated, so nothing was decided or sent.
    public const string CouldNotReadDevices = "Could not read the audio devices. Try again.";

    // Connect, and every render endpoint is DISABLED (or NOTPRESENT with at least one DISABLED), so no request
    // could turn into an ACTIVE endpoint; or disconnect, and every render endpoint is DISABLED, so no disconnect
    // could be seen.
    public const string OutputTurnedOff = "The AirPods output is turned off in Sound settings.";

    // No endpoint in the device's container.
    public const string NotFound = "AirPods not found. Connect them to this PC once from Windows Bluetooth settings.";

    // Blocked and the Allow could not run because the gate task is missing. Used by the block coordinator.
    public const string BootBlockNotSetUp = "Boot block is not set up yet. Choose Set up Earshot.";

    // The device changed mid-operation (AUDCLNT_E_DEVICE_INVALIDATED on the A2DP side), or its render endpoint
    // went, or became NOTPRESENT during a connect.
    public const string WentAway = "The AirPods went away. Try again.";

    // A request was accepted but no state change came in time (disconnect).
    public const string DidNotDisconnect = "The AirPods did not disconnect in time. Try again.";
}
