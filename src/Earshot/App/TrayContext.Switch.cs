using Earshot.Contracts;
using Earshot.Hotkeys;

namespace Earshot.App;

// The tray's side of switching the AirPods between the phone and this PC: the two shortcuts that state a direction,
// and the one line each switch leaves in the log. A switch is not a third verb. To this PC is the connect and to the
// phone is the disconnect, both entering through ToggleAsync, so every guard the left click has (closing, a session
// end, a menu action in flight, a link that is changing, no target, safe mode) applies to the shortcuts unchanged.
internal sealed partial class TrayContext
{
    // Whether a toggle asked for now replaces the one in flight. A click does the opposite of what is running, but
    // not twice inside the double-click time, so a double click is one request. A shortcut states its end state:
    // the same direction as the one in flight changes nothing and is ignored; the other direction replaces it at
    // once, since a keypress made without looking must be honoured whenever it comes.
    private bool SupersedesInFlightToggle(bool? wanted, long clickedAt)
    {
        if (wanted is { } direction)
        {
            return !_toggleSuperseded && direction != _toggleConnect;
        }

        return !(_toggleSuperseded || clickedAt - _toggleClickedAt < (long)_doubleClickTime.TotalMilliseconds);
    }

    // What a settings page binds its shortcut boxes to: a copy of the hotkey settings the page will save, with each
    // box's chord, set, clear and "did it fail to register", the last read from what the running registration did.
    internal HotkeyBindingModel HotkeyBindings(HotkeySettings editing) => new(editing, () => _hotkeys.CurrentOutcomes);

    // The shortcut for a direction fired. It asks for that end state whatever the tray last believed: pressed when
    // the AirPods are already there, the coordinator confirms without sending anything and the card says so.
    private void OnSwitchHotkey(HotkeyAction action)
    {
        bool toPc = action == HotkeyAction.SwitchToPc;
        _log.Info("Hotkey: switch to " + (toPc ? "this PC." : "the phone."));
        StartToggle(viaHotkey: true, toPc ? SwitchTrigger.ShortcutToPc : SwitchTrigger.ShortcutToPhone, wanted: toPc);
    }

    // Writes the measurement of a finished switch next to the report line. A report with no timeline writes nothing.
    // A line that cannot be built is dropped with the exception's type: the switch has already happened and must not
    // be undone or hidden by its own measurement. This guards a formatter, not COM, CfgMgr32, Bluetooth or Task
    // Scheduler.
    internal static void LogSwitch(ILog log, ToggleReport report)
    {
        if (report.Timeline is not { } timeline)
        {
            return;
        }

        try
        {
            string line = SwitchTimelineText.Format(timeline);
            log.Write(SwitchTimelineText.IsGood(timeline) ? LogLevel.Info : LogLevel.Warn, line);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Warn("Switch timing: the line could not be written (" + ex.GetType().Name + "). The switch itself is not affected.");
        }
    }
}
