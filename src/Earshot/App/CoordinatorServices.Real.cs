using System.ComponentModel;
using System.Diagnostics;
using Earshot.Boot;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.App;

// The lock state from WTSQuerySessionInformation. A read that fails is Unknown, which every rule takes as locked, and the raw
// code is logged once per change of answer.
internal sealed class WtsSessionState : ISessionState
{
    private readonly ILog _log;
    private SessionLockState? _last;

    public WtsSessionState(ILog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    public SessionLockState Current
    {
        get
        {
            WtsSession.Reading read = WtsSession.Read();
            SessionLockState state = read.Locked switch
            {
                true => SessionLockState.Locked,
                false => SessionLockState.Unlocked,
                _ => SessionLockState.Unknown,
            };
            if (state != _last)
            {
                _last = state;
                if (state == SessionLockState.Unknown)
                {
                    _log.Warn("The session lock state could not be read: " + read.Step + ", code " + read.Code + ", flags " + read.Flags + ". It counts as locked.");
                }
                else if (read.Step.Length > 0)
                {
                    _log.Info("The session counts as locked: " + read.Step + ", code " + read.Code + ", flags " + read.Flags + ".");
                }
            }

            return state;
        }
    }
}

// The device node's link bit for the pinned AirPods, read with CfgMgr32 (no request to the device). Disabled nodes have no link.
internal sealed class NodeLinkProbe : ILinkProbe
{
    private readonly INodeReader _nodes;
    private readonly ISettingsStore _settings;
    private readonly ILog _log;

    public NodeLinkProbe(INodeReader nodes, ISettingsStore settings, ILog log)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);
        _nodes = nodes;
        _settings = settings;
        _log = log;
    }

    public Task<LinkState> ReadAsync(CancellationToken ct) => Task.Run(Read, ct);

    internal LinkState Read()
    {
        EarshotSettings settings = _settings.Current;
        NodeScanResult scan = NodeScan.FindTargets(_nodes, settings.PinnedContainerId, settings.PinnedAddress);
        TargetNode? device = scan.Targets.FirstOrDefault(t => t.IsDeviceNode);
        if (!scan.Listed || device is null)
        {
            return LinkState.Unknown;
        }

        uint cr = _nodes.Locate(device.InstanceId, includeNonPresent: false, out uint devInst);
        if (cr == CfgMgr32.CR_NO_SUCH_DEVNODE)
        {
            return LinkState.Disconnected;
        }

        if (cr == CfgMgr32.CR_SUCCESS)
        {
            cr = _nodes.GetStatus(devInst, out uint status, out uint problem);
            if (cr == CfgMgr32.CR_SUCCESS)
            {
                bool disabled = (status & CfgMgr32.DN_HAS_PROBLEM) != 0 && problem == CfgMgr32.CM_PROB_DISABLED;
                return disabled || (status & CfgMgr32.DN_DEVICE_DISCONNECTED) != 0 ? LinkState.Disconnected : LinkState.Connected;
            }
        }

        _log.Warn("The device node's link state could not be read: " + NativeCodes.ConfigRet(cr) + ".");
        return LinkState.Unknown;
    }
}

// Opens ms-settings:bluetooth through the shell. A failure is a step with its raw code, never swallowed.
internal sealed class ShellSettingsLauncher : IWindowsSettingsLauncher
{
    internal const string Uri = "ms-settings:bluetooth";

    public StepOutcome OpenBluetoothSettings()
    {
        try
        {
            using Process? started = Process.Start(new ProcessStartInfo(Uri) { UseShellExecute = true });
            return StepOutcomes.FromWin32("open-settings", 0, Uri);
        }
        catch (Win32Exception ex)
        {
            return StepOutcomes.FromWin32("open-settings", (uint)ex.NativeErrorCode, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return StepOutcomes.FromHResult("open-settings", ex.HResult, ex.Message, ok: false);
        }
    }
}

// Device hints from wherever the broadcast is heard. Nothing is set by default, which reads as Unknown; whoever owns the
// broadcast sets Source once it exists.
internal sealed class SettableDeviceHints : IDeviceHints
{
    public Func<DeviceHint>? Source { get; set; }

    public DeviceHint Read() => Source?.Invoke() ?? DeviceHint.Unknown;
}
