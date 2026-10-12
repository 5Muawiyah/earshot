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

    // A pair made in Windows' settings, or by a Fix run started from Setup or the install script, arrives as a new device group.
    // The check for AirPods Earshot does not protect then runs again, after NewDeviceSettle, instead of waiting for the next
    // sign-in. The card about them keeps its once-per-run rule. UI thread only.
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
