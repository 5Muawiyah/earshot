using Earshot.Widget;

namespace Earshot.App;

// What the radio side of the PC knows about the owner's AirPods, read from the widget's broadcast state and nothing else.
// It is read only when a connect has timed out, and it only reads: no scan, no request to a device, nothing is started or
// changed. It decides nothing by itself; the failure classifier turns a hint into whose side a failed connect was on.
//
//   Unknown          the broadcast cannot say: the listener is off or stopped, or no pair is linked, so there is no pair
//                    whose silence could mean anything
//   NotHeard         the linked pair has not been heard for longer than the broadcast counts as fresh
//   InUseElsewhere   heard, and its ear state says it is in use on a device other than this PC
//   LidClosed        heard, and the case-open bit has been seen and says the lid is shut
//   HeardNearby      heard, and nothing says it is away or shut: the PC's side is then the one left to look at
//
// The linked pair is the one the existing link rule chose (a case opened by the PC); this adds no link rule and does not touch
// the decoding of a message.
internal sealed class BroadcastDeviceHints : IDeviceHints
{
    // The same window the card uses to call a broadcast reading current (BatteryFreshness): a pair heard within it is heard.
    public static TimeSpan HeardWindow => BatteryFreshness.FreshWindow;

    private readonly Func<WidgetSnapshot?> _snapshot;
    private readonly TimeProvider _time;

    public BroadcastDeviceHints(Func<WidgetSnapshot?> snapshot, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(time);
        _snapshot = snapshot;
        _time = time;
    }

    public DeviceHint Read()
    {
        WidgetSnapshot? snapshot = _snapshot();
        if (snapshot is null || snapshot.Watcher != WidgetWatcherState.Started || snapshot.Selection != BroadcastSelectionState.Linked)
        {
            return DeviceHint.Unknown;
        }

        if (snapshot.BatteryReadAt is not DateTimeOffset heardAt || _time.GetUtcNow() - heardAt > HeardWindow)
        {
            return DeviceHint.NotHeard;
        }

        if (snapshot.Where == AirPodsWhere.Elsewhere)
        {
            return DeviceHint.InUseElsewhere;
        }

        return snapshot.LidOpen == false ? DeviceHint.LidClosed : DeviceHint.HeardNearby;
    }

    // The hints the tray gives the coordinator: read from the widget once it exists (it is built when the tray's UI is), and
    // Unknown until then.
    public static SettableDeviceHints For(Func<IWidgetStatus?> widget, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(widget);
        var source = new BroadcastDeviceHints(() => widget()?.Current, time);
        return new SettableDeviceHints { Source = source.Read };
    }
}
