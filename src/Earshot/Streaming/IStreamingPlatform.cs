namespace Earshot.Streaming;

// The whole of Windows, as far as this feature is concerned. One real implementation
// (WindowsStreamingPlatform), one fake in the tests. No other code in the application touches WinRT.
//
// No member throws for anything Windows did: each returns a record whose Step carries the raw HRESULT and
// the exception type. The one exception a member may throw is OperationCanceledException, when the token it
// was given is cancelled, so the caller can tell its own time limit from a failure.
internal interface IStreamingPlatform
{
    StreamingSupportCheck CheckSupport();

    // A read of the paired devices Windows says can stream to this PC. It reads the device store; it never
    // scans for, pairs with or connects to anything.
    Task<StreamingDiscovery> ListStreamCapableDevicesAsync(CancellationToken cancellationToken);

    // Creates the connection and enables it. Does not start audio. A device that is already enabled stays as
    // it is and reads as Enabled.
    //
    // The platform lets go of nothing by itself. An enable that fails once the connection exists, or that is
    // cancelled, leaves that connection held, and the caller calls Release for the id: that is the one place a
    // connection is released, so the one place its outcome is recorded (StreamingCoordinator.Release). A platform
    // that released quietly on its own failure paths had nowhere to put the outcome when that release failed too.
    Task<StreamingEnableOutcome> EnableAsync(string deviceId, CancellationToken cancellationToken);

    // Opens a connection that EnableAsync already enabled. Audio starts here.
    Task<StreamingOpenOutcome> OpenAsync(string deviceId, CancellationToken cancellationToken);

    // Disposes the connection. Safe to call when nothing is enabled, which reads as Released false and not Failed.
    // Never throws. When Windows does not confirm (Failed), the connection is still held and the next Release for
    // the id tries the same connection again: nothing is dropped unconfirmed.
    StreamingReleaseOutcome Release(string deviceId);

    // Raised on whatever thread Windows calls back on. See StreamingCoordinator.
    event EventHandler<StreamingLinkChanged>? LinkChanged;
}
