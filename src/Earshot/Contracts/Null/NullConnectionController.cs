namespace Earshot.Contracts.Null;

// Stands in until connect and disconnect are wired. Never touches a device.
public sealed class NullConnectionController : IConnectionController
{
    public Task<ConnectResult> ConnectAsync(Guid containerId, CancellationToken ct = default) =>
        Task.FromResult(NullResults.ConnectNotAttempted("connect"));

    public Task<ConnectResult> DisconnectAsync(Guid containerId, CancellationToken ct = default) =>
        Task.FromResult(NullResults.ConnectNotAttempted("disconnect"));
}
