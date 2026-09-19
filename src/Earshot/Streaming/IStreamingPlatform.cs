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
    Task<StreamingEnableOutcome> EnableAsync(string deviceId, CancellationToken cancellationToken);

    // Opens a connection that EnableAsync already enabled. Audio starts here.
    Task<StreamingOpenOutcome> OpenAsync(string deviceId, CancellationToken cancellationToken);

    // Disposes the connection. Safe to call when nothing is enabled. Never throws.
    StreamingReleaseOutcome Release(string deviceId);

    // Raised on whatever thread Windows calls back on. See StreamingCoordinator.
    event EventHandler<StreamingLinkChanged>? LinkChanged;
}
