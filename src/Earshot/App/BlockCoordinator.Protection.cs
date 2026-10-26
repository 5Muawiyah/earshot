using Earshot.Contracts;

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

    // The tray's look at a pairing that did not finish, which the start checks wait for, ended without showing anything. The
    // check for AirPods Earshot does not protect runs now instead of at the next start: the tray compared with its own pin,
    // and the gate may still name another pair. UI thread only.
    public void CheckProtectionAfterPairRecovery()
    {
        if (_closing || _disposed || !_started || _services.TrayPairing.Active)
        {
            return;
        }

        _log.Info("The tray's look at a pairing that did not finish showed nothing, so the check for AirPods that Earshot does not protect runs now.");
        _ = CheckProtectionAsync();
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
        _ = RunFixStartChecksAsync();
    }
}
