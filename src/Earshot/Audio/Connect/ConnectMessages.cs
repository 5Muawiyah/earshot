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

    // No filter accepted the request, or the audio devices could not be read. The block coordinator owns
    // the other way (Allow then reconnect, or Block).
    public const string CouldNotReachDriver = "Could not reach the AirPods audio driver. Trying another way.";

    // Every endpoint is NOTPRESENT. The block coordinator allows the device nodes first, then connects.
    public const string AllowingFirst = "Allowing first, then connecting.";

    // No endpoint in the device's container.
    public const string NotFound = "AirPods not found. Connect them to this PC once from Windows Bluetooth settings.";

    // Blocked and the Allow could not run because the gate task is missing. Used by the block coordinator.
    public const string BootBlockNotSetUp = "Boot block is not set up yet. Choose Set up Earshot.";

    // The device changed mid-operation (AUDCLNT_E_DEVICE_INVALIDATED), or its render endpoint went.
    public const string WentAway = "The AirPods went away. Try again.";

    // A request was accepted but no state change came in time (disconnect).
    public const string DidNotDisconnect = "The AirPods did not disconnect in time. Try again.";
}
