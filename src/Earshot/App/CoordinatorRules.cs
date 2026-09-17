using Earshot.Audio.Connect;
using Earshot.AudioProtection;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;

namespace Earshot.App;

// Pure decisions behind the block coordinator, over snapshots and results only.
internal static class CoordinatorRules
{
    // The render side of container in one snapshot. Only a snapshot whose enumeration worked is an observation;
    // a failed or missing read is Unknown and never drives a block, an allow or a pin. A container with no
    // endpoints in a good read is NotActive: nothing of it can be playing.
    // https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-state-xxx-constants
    public static RenderState RenderOf(DeviceSnapshot snapshot, Guid container)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.ReadStatus != SnapshotReadStatus.Ok || !NodeMatch.IsValidTargetContainer(container))
        {
            return RenderState.Unknown;
        }

        return EndpointsOf(snapshot, container).Any(e => e.Flow == EndpointFlow.Render && e.State == EndpointState.Active)
            ? RenderState.Active
            : RenderState.NotActive;
    }

    // True when a good read shows a render endpoint of container that can take a connect request: ACTIVE or
    // UNPLUGGED. After an allow this is what shows the enabled nodes have brought the endpoints back.
    public static bool RenderEndpointPresent(DeviceSnapshot snapshot, Guid container)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.ReadStatus == SnapshotReadStatus.Ok &&
               EndpointsOf(snapshot, container).Any(e => e.Flow == EndpointFlow.Render && e.State is EndpointState.Active or EndpointState.Unplugged);
    }

    // True when a good read shows a capture endpoint of container that is not NOTPRESENT: the Hands-Free side is
    // back after protection was turned off.
    public static bool CaptureEndpointPresent(DeviceSnapshot snapshot, Guid container)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.ReadStatus == SnapshotReadStatus.Ok &&
               EndpointsOf(snapshot, container).Any(e => e.Flow == EndpointFlow.Capture && e.State is EndpointState.Active or EndpointState.Unplugged);
    }

    // True when the result shows the A2DP filter was sent the reconnect request and turned it down: a ks step
    // whose role is A2DP with a code that is neither S_OK nor NOT_ATTEMPTED (nothing sent). The role comes from
    // the step detail, not from the driver-chosen filter name.
    public static bool A2dpRejected(ConnectResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Steps.Any(s =>
            s.Step.StartsWith(KsConnectPath.ReconnectStep + ":", StringComparison.Ordinal) &&
            KsConnectPath.RoleOfStep(s) == FilterRole.A2dp &&
            s.Code != 0 &&
            s.Code != NativeCodes.NotAttempted);
    }

    // The container the idle and start-up rules watch: the device the gate blocks (device.json, reported by the
    // status read), then the pinned device, then the resolved target. Guid.Empty when none is a device.
    public static Guid WatchedContainer(BootBlockStatus? status, EarshotSettings settings, DeviceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (status is not null && NodeMatch.IsValidTargetContainer(status.TargetContainerId))
        {
            return status.TargetContainerId;
        }

        if (NodeMatch.IsValidTargetContainer(settings.PinnedContainerId))
        {
            return settings.PinnedContainerId;
        }

        Guid resolved = snapshot.Target?.ContainerId ?? Guid.Empty;
        return NodeMatch.IsValidTargetContainer(resolved) ? resolved : Guid.Empty;
    }

    // True when the nodes are known to be enabled, at least in part: the only node states an automatic block
    // acts on. Unknown, NotFound and NotSetUp are not observations of enabled nodes.
    public static bool NodesEnabled(BootBlockStatus? status) =>
        status?.State is BlockState.Allowed or BlockState.Mixed;

    // Why a connect of container that found every render endpoint NOTPRESENT cannot allow first, or null when it
    // can. Allow first is decided from the node state, not from NOTPRESENT alone: an adapter that is off or
    // removed also reads NOTPRESENT. The nodes the gate would allow must be this device's: when the gate still
    // pins another one (a device change it did not take), allowing would enable the wrong device.
    public static string? AllowFirstRefusal(BootBlockStatus? status, Guid container) => status?.State switch
    {
        null => BlockCoordinator.BlockStatusUnreadableMessage,
        BlockState.Blocked or BlockState.Mixed => status.TargetContainerId == container ? null : BlockCoordinator.OtherDeviceMessage,
        BlockState.NotSetUp => ConnectMessages.BootBlockNotSetUp,
        BlockState.NotFound => ConnectMessages.NotFound,
        _ => BlockCoordinator.NotAvailableMessage,
    };

    // The message for a connect that did not reach ACTIVE and whose other way is not taken. The controller's
    // "Trying another way" and "Allowing first" promise actions that only the coordinator takes, so they are
    // replaced whenever that action does not follow. Once the clean-up has blocked the nodes again (blockedAgain),
    // the connect cannot still be going, so "Still connecting" is replaced too.
    public static string ConnectFailureMessage(ConnectResult result, bool blockedAgain)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Outcome switch
        {
            ConnectOutcome.NoFiltersResponded => BlockCoordinator.CouldNotReachDriverMessage,
            ConnectOutcome.NodesBlocked => BlockCoordinator.DidNotComeBackMessage,
            ConnectOutcome.AttemptedTimedOut when blockedAgain => BlockCoordinator.DidNotConnectMessage,
            _ when result.UserMessage == ConnectMessages.CouldNotReachDriver => BlockCoordinator.CouldNotReachDriverMessage,
            _ when result.UserMessage == ConnectMessages.AllowingFirst => BlockCoordinator.DidNotComeBackMessage,
            _ when blockedAgain && result.UserMessage == ConnectMessages.StillConnecting => BlockCoordinator.DidNotConnectMessage,
            _ => result.UserMessage,
        };
    }

    // The step names TaskSchedulerGate records for a run the Task Scheduler accepted (a prefix, followed by the
    // task path) and for the end of that run. The gate records the second only once it has seen the run end.
    internal const string TaskRunStepPrefix = "task-run:";
    internal const string TaskEndedStep = "task-last-result";

    // True when a gate change was sent but its end was not seen, so it may still run: the Task Scheduler accepted
    // the run (a task-run step that worked) and no task-last-result step shows it ended, because the wait for it
    // ran out, was stopped, or a task state read failed while it was polled. A request refused before RunEx, a run
    // seen to end, and a result from a controller that sends nothing through a task are all false.
    // https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-iregisteredtask-runex
    // https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-iregisteredtask-get_state
    public static bool MayStillRun(ControllerResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        bool sent = result.Steps.Any(s => s.Ok && s.Step.StartsWith(TaskRunStepPrefix, StringComparison.Ordinal));
        bool ended = result.Steps.Any(s => string.Equals(s.Step, TaskEndedStep, StringComparison.Ordinal));
        return sent && !ended;
    }

    // Why a set-device did not move the pin, from the gate's own exit code, which BlockController records as a step.
    public static SetDeviceRefusal SetDeviceRefusalOf(ControllerResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsSuccess)
        {
            return SetDeviceRefusal.None;
        }

        StepOutcome? exit = result.Steps.LastOrDefault(s => string.Equals(s.Step, BlockController.SetDeviceExitStep, StringComparison.Ordinal));
        return exit?.Code switch
        {
            (int)GateExitCode.OtherDeviceBlocked => SetDeviceRefusal.OldDeviceBlocked,
            (int)GateExitCode.OtherDeviceProtected => SetDeviceRefusal.OldDeviceProtected,
            _ => SetDeviceRefusal.None,
        };
    }

    // The message for a disconnect that was not observed, with no other way taken (Block at boot off or not known).
    public static string DisconnectFailureMessage(ConnectResult? result) =>
        result is null ? ConnectMessages.DidNotDisconnect
        : result.Outcome == ConnectOutcome.NoFiltersResponded || result.UserMessage == ConnectMessages.CouldNotReachDriver
            ? BlockCoordinator.CouldNotReachDriverMessage
            : result.UserMessage;

    // True when a protect-on result leaves the services protected on its own evidence: Success or AlreadyInState.
    // Partial needs a read of the services to tell "protected, a step failed" from "Headset still on".
    public static bool ProtectedByResult(ControllerResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Status is OpStatus.Success or OpStatus.AlreadyInState;
    }

    public static NodePhase PhaseOf(BootBlockStatus? status) =>
        ProtectionPolicy.PhaseOf(status?.State ?? BlockState.Unknown);

    private static IReadOnlyList<AudioEndpoint> EndpointsOf(DeviceSnapshot snapshot, Guid container)
    {
        DeviceModel? group = snapshot.AllGroups.FirstOrDefault(g => g.ContainerId == container) ??
                             (snapshot.Target?.ContainerId == container ? snapshot.Target : null);
        return group?.Endpoints ?? Array.Empty<AudioEndpoint>();
    }
}
