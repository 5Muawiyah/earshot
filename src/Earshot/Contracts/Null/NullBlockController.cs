namespace Earshot.Contracts.Null;

// Stands in until the boot block is wired. Reports NotSetUp, because without the gate
// blocking cannot be requested, and never touches a device node.
public sealed class NullBlockController : IBlockController
{
    public bool IsSetUp => false;

    public Task<BootBlockStatus> GetStatusAsync(CancellationToken ct = default) =>
        Task.FromResult(new BootBlockStatus(
            State: BlockState.NotSetUp,
            TargetContainerId: Guid.Empty,
            Nodes: Array.Empty<BluetoothNode>(),
            TasksInstalled: false,
            // Before setup the menu shows the shipped default, which lives in GateConfig.
            BlockAtBoot: new GateConfig().BlockAtBoot));

    public Task<ControllerResult> BlockAsync(CancellationToken ct = default) =>
        Task.FromResult(NullResults.NotAttempted("block"));

    public Task<ControllerResult> AllowAsync(CancellationToken ct = default) =>
        Task.FromResult(NullResults.NotAttempted("allow"));

    public Task<ControllerResult> SetBlockAtBootAsync(bool blockAtBoot, CancellationToken ct = default) =>
        Task.FromResult(NullResults.NotAttempted(blockAtBoot ? "setboot-on" : "setboot-off"));

    public Task<ControllerResult> SetDeviceAsync(string address12, CancellationToken ct = default) =>
        Task.FromResult(NullResults.NotAttempted("set-device"));

    public Task<ControllerResult> RunSetupAsync(CancellationToken ct = default) =>
        Task.FromResult(NullResults.NotAttempted("install"));

    public Task<ControllerResult> UninstallAsync(CancellationToken ct = default) =>
        Task.FromResult(NullResults.NotAttempted("uninstall"));
}
