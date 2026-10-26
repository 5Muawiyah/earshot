using Earshot.Contracts;
using Earshot.Tray;

namespace Earshot.App;

internal sealed partial class BlockCoordinator
{
    // How long a new device group is given to settle before the check for AirPods Earshot does not protect runs again. A
    // waiting budget, not a measured figure.
    public static readonly TimeSpan NewDeviceSettle = TimeSpan.FromSeconds(5);

    // The device groups of the last good enumeration; null until the first, which the start checks cover.
    private HashSet<Guid>? _knownGroups;
    private bool _newDeviceCheckPending;

    // A pair made in Windows' settings, or by a Fix run started from Setup or the install script, is expected to arrive as a new
    // device group (reasoned from how pairing adds a device container; no live run has shown it).
    // The check for AirPods Earshot does not protect then runs again, after NewDeviceSettle, instead of waiting for the next
    // sign-in. The card about them keeps its once-per-run rule. UI thread only.
    // Asked when a new device group has settled, before the check for unprotected AirPods: true when it showed the person
    // something about the pairs. Set by the tray. UI thread.
    internal Func<CancellationToken, Task<bool>>? NewDeviceHook { get; set; }

    // The tray's own look at the pinned pair at start: true when it showed the person a card about the pairs (a pin Windows no
    // longer holds, or none paired), which the card about unprotected AirPods would only replace. Asked at start only.
    internal Func<Task<bool>>? StartLookHook { get; set; }

    private void NoteNewDeviceGroups(DeviceSnapshot snapshot)
    {
        if (snapshot.ReadStatus != SnapshotReadStatus.Ok)
        {
            return;
        }

        var groups = snapshot.AllGroups.Select(g => g.ContainerId).ToHashSet();
        bool added = _knownGroups is { } known && groups.Any(id => !known.Contains(id));
        _knownGroups = groups;
        if (added && !_newDeviceCheckPending && _started && !_closing && !_disposed)
        {
            _newDeviceCheckPending = true;
            _ = CheckProtectionAfterNewDeviceAsync();
        }
    }

    private async Task CheckProtectionAfterNewDeviceAsync()
    {
        try
        {
            await Task.Delay(NewDeviceSettle, _time, CancellationToken.None);
            if (_closing || _disposed)
            {
                return;
            }

            // A Fix connection run pairing the AirPods again makes the new group itself; it adopts the pair or says it could not.
            if (_services.RePair.IsOpen)
            {
                _log.Info("A new device appeared while a Fix connection run is pairing the AirPods again; that run answers for it.");
                return;
            }

            if (_services.TrayPairing.Active)
            {
                _log.Info("A new device appeared while the tray's Pair AirPods runs; that run answers for it.");
                return;
            }

            // The tray looks at whether the pinned pair is still paired. When that look told the person something, it has said what
            // there is to say about the pairs, and a second card about the same new pair would only repeat it.
            if (NewDeviceHook is { } hook && await hook(CancellationToken.None))
            {
                _log.Info("A new device appeared; the tray answered for it, so the check for AirPods that Earshot does not protect did not run.");
                return;
            }

            // A pairing the tray started while its hook looked answers for the new device itself.
            if (_services.TrayPairing.Active || _closing || _disposed)
            {
                _log.Info("A new device appeared while the tray's Pair AirPods runs; that run answers for it.");
                return;
            }

            _log.Info("A new device appeared; the check for AirPods that Earshot does not protect runs again.");
            ProtectionReading reading = await _services.Protection.ReadAsync(CancellationToken.None);
            if (reading.AnyUnprotected)
            {
                ShowNotProtectedCard(reading.Unprotected);
            }
        }
        catch (Exception ex)
        {
            _log.Error("The check for AirPods that Earshot does not protect, after a new device appeared, failed (" + ex.GetType().Name +
                ", HRESULT 0x" + ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ").", ex);
        }
        finally
        {
            _newDeviceCheckPending = false;
        }
    }

    // A run of the tray's own that the checks for unprotected AirPods waited for has ended and may have left a pair Earshot does
    // not protect: its look at a pairing that did not finish showed nothing (it compared with the tray's pin, and the gate may
    // still name another pair), the person kept the pinned pair, or the pairing stopped on something unexpected. The check runs
    // now instead of at the next start or the next new device. UI thread only.
    public void CheckProtectionAfterTrayPairing(string why)
    {
        if (_closing || _disposed || !_started || _services.TrayPairing.Active)
        {
            return;
        }

        _log.Info("The check for AirPods that Earshot does not protect runs now: " + why + ".");
        _ = CheckProtectionAsync();
    }

    // The tray's Pair AirPods, or its look at a pairing that did not finish, has let go. While it ran the idle rule read
    // Unsettled and its rechecks doubled, so the next read could be up to IdleRetryLimit away with the nodes enabled; the state
    // is read now instead, and the start-up block a look held back is tried again. UI thread only.
    public void TrayPairingEnded()
    {
        if (_closing || _disposed || !_started || _services.TrayPairing.Active)
        {
            return;
        }

        _log.Info("The tray's pairing has ended, so the state is read again now.");
        StopRechecks(settled: true);
        _ = ReadAgainAfterTrayPairingAsync();
    }

    private async Task ReadAgainAfterTrayPairingAsync()
    {
        try
        {
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            StepOutcome step = StepOutcomes.FromHResult("recheck", ex.HResult, ex.GetType().Name + ": " + ex.Message, ok: false);
            _log.Error("Reading the state again after the tray's pairing failed. " + TrayReport.DescribeStep(step), ex);
        }
    }

    // A Fix connection run started from the tray has ended. It may have left a run half done or a new pair Earshot does not
    // protect (an adoption that failed), so the checks made at start run again now rather than at the next sign-in, and the
    // card about unprotected AirPods may be shown again. UI thread only, like the rest of the coordinator.
    public void RecheckProtectionAfterFix()
    {
        if (_closing || _disposed || !_started)
        {
            return;
        }

        _log.Info("A Fix connection run ended; the start checks run again.");
        _notProtectedShown = false;
        _ = RunFixStartChecksAsync(atStart: false);
    }
}
