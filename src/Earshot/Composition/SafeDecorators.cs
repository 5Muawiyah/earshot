using Earshot.Contracts;

namespace Earshot.Composition;

// Safe mode (EARSHOT_SAFE_MODE). Read-only members pass straight through to the real controller.
// Every member that would change a device, a device node, a Bluetooth service, a scheduled task or
// start an elevation is refused: it returns NotAttempted (or ConnectOutcome.Failed), says
// "Safe mode: no device actions." and logs the refusal. The real controller is never called for
// those members.
internal static class SafeDecorators
{
    public const string Message = "Safe mode: no device actions.";

    public static IConnectionController Wrap(IConnectionController inner, ILog log) =>
        inner as SafeConnectionController ?? new SafeConnectionController(inner, log);

    public static IBlockController Wrap(IBlockController inner, ILog log) =>
        inner as SafeBlockController ?? new SafeBlockController(inner, log);

    public static IAudioProtectionController Wrap(IAudioProtectionController inner, ILog log) =>
        inner as SafeAudioProtectionController ?? new SafeAudioProtectionController(inner, log);

    internal static StepOutcome RefusedStep(string action) =>
        new("safe-mode:" + action, Ok: false, Code: 0, CodeName: "NOT_ATTEMPTED", Detail: Message);

    internal static ControllerResult Refuse(ILog log, string action)
    {
        log.Warn(Message + " Refused: " + action + ".");
        return new ControllerResult(OpStatus.NotAttempted, Message, new[] { RefusedStep(action) });
    }

    internal static ConnectResult RefuseConnect(ILog log, string action)
    {
        log.Warn(Message + " Refused: " + action + ".");
        return new ConnectResult(ConnectOutcome.Failed, Message, new[] { RefusedStep(action) });
    }
}

internal sealed class SafeConnectionController : IConnectionController
{
    private readonly ILog _log;

    public SafeConnectionController(IConnectionController inner, ILog log)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(log);
        Inner = inner;
        _log = log;
    }

    public IConnectionController Inner { get; }

    public Task<ConnectResult> ConnectAsync(Guid containerId, CancellationToken ct = default) =>
        Task.FromResult(SafeDecorators.RefuseConnect(_log, "connect"));

    public Task<ConnectResult> DisconnectAsync(Guid containerId, CancellationToken ct = default) =>
        Task.FromResult(SafeDecorators.RefuseConnect(_log, "disconnect"));
}

internal sealed class SafeBlockController : IBlockController
{
    private readonly ILog _log;

    public SafeBlockController(IBlockController inner, ILog log)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(log);
        Inner = inner;
        _log = log;
    }

    public IBlockController Inner { get; }

    public bool IsSetUp => Inner.IsSetUp;

    public Task<BootBlockStatus> GetStatusAsync(CancellationToken ct = default) => Inner.GetStatusAsync(ct);

    public Task<ControllerResult> BlockAsync(CancellationToken ct = default) =>
        Task.FromResult(SafeDecorators.Refuse(_log, "block"));

    public Task<ControllerResult> AllowAsync(CancellationToken ct = default) =>
        Task.FromResult(SafeDecorators.Refuse(_log, "allow"));

    public Task<ControllerResult> SetBlockAtBootAsync(bool blockAtBoot, CancellationToken ct = default) =>
        Task.FromResult(SafeDecorators.Refuse(_log, blockAtBoot ? "setboot-on" : "setboot-off"));

    public Task<ControllerResult> SetDeviceAsync(string address12, CancellationToken ct = default) =>
        Task.FromResult(SafeDecorators.Refuse(_log, "set-device"));

    public Task<ControllerResult> RunSetupAsync(CancellationToken ct = default) =>
        Task.FromResult(SafeDecorators.Refuse(_log, "install"));

    public Task<ControllerResult> UninstallAsync(CancellationToken ct = default) =>
        Task.FromResult(SafeDecorators.Refuse(_log, "uninstall"));
}

internal sealed class SafeAudioProtectionController : IAudioProtectionController
{
    private readonly ILog _log;

    public SafeAudioProtectionController(IAudioProtectionController inner, ILog log)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(log);
        Inner = inner;
        _log = log;
    }

    public IAudioProtectionController Inner { get; }

    public Task<AudioProtectionSnapshot> GetStatusAsync(CancellationToken ct = default) => Inner.GetStatusAsync(ct);

    public Task<ControllerResult> ApplyAsync(bool protect, CancellationToken ct = default) =>
        Task.FromResult(SafeDecorators.Refuse(_log, protect ? "protect-on" : "protect-off"));
}
