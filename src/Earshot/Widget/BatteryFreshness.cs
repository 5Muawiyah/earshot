namespace Earshot.Widget;

internal enum BatterySource { Broadcast, Windows }

// One part as it is shown: the value as last read, and whether that read is current. A part that is not fresh is
// drawn greyed with its age; it is never drawn as current.
internal readonly record struct ShownPart(int? Percent, bool? Charging, bool Fresh, DateTimeOffset? ReadAt)
{
    public static ShownPart None => new(null, null, false, null);

    public bool HasValue => Percent is not null;
}

// The figure the gauge draws: the number, where it came from and, for the broadcast, the buds it was taken from.
// ReadAt is the oldest read among the parts it is drawn from.
internal sealed record GaugeFigure(int Percent, BatterySource Source, int? Left, int? Right, bool Charging, DateTimeOffset ReadAt);

// What every surface shows of the battery, worked out once: the card, the gauge and the low battery alert all
// read this, so "what is shown" has one definition.
//   Left, Right, Case: the values as last read, each with whether it is fresh.
//   WindowsPercent: Windows' own Hands-Free figure, set only when it is what is shown: no bud has a fresh
//   broadcast value and the figure is current. It is one figure for the headset, never a bud's or the case's.
//   Gauge: the number the gauge draws, or null for the mark alone.
internal sealed record ShownBattery(
    ShownPart Left, ShownPart Right, ShownPart Case, int? WindowsPercent, DateTimeOffset? WindowsReadAt, GaugeFigure? Gauge);

// How old a battery value may be and still be shown as current, or at all. Pure: now is handed in.
internal static class BatteryFreshness
{
    // A value read within this long is current; older, it is greyed and its age is said. In use the set sends
    // about 35 documented messages a minute (one every 1.7 s), so 30 s holds about 17; the longest gap from one
    // sender in any capture is 6.14 s and for a merged set 3.11 s. A closed case sends nothing, so values go
    // grey half a minute after the lid closes, which is what greying is for.
    public static readonly TimeSpan FreshWindow = TimeSpan.FromSeconds(30);

    // A value older than this counts as no recent reading: the gauge drops it.
    public static readonly TimeSpan RecentWindow = TimeSpan.FromHours(1);

    // Windows' figure counts only while the AirPods are on this PC and it was read within two poll periods.
    // Nothing says Windows clears it on disconnect, so it is never shown for AirPods that are not here.
    public static readonly TimeSpan HeadsetCurrentWindow = TimeSpan.FromSeconds(120);

    public static bool IsFresh(PartReading part, DateTimeOffset now) => Within(part, now, FreshWindow);

    public static bool IsRecent(PartReading part, DateTimeOffset now) => Within(part, now, RecentWindow);

    // Windows' figure is current: on this PC, with a value, read within HeadsetCurrentWindow.
    public static bool IsHeadsetCurrent(PartReading headset, bool onThisPc, DateTimeOffset now) =>
        onThisPc && Within(headset, now, HeadsetCurrentWindow);

    private static bool Within(PartReading part, DateTimeOffset now, TimeSpan window)
    {
        if (part.Percent is null || part.ReadAt is not { } at)
        {
            return false;
        }

        TimeSpan age = now - at;
        return age <= window;
    }

    public static ShownBattery Shown(WidgetSnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Shown(snapshot.Left, snapshot.Right, snapshot.Case, snapshot.Headset, snapshot.Where == AirPodsWhere.ThisPc, now);
    }

    public static ShownBattery Shown(
        PartReading left, PartReading right, PartReading caseReading, PartReading headset, bool onThisPc, DateTimeOffset now)
    {
        ShownPart l = Part(left, now);
        ShownPart r = Part(right, now);
        ShownPart c = Part(caseReading, now);

        bool freshBud = l.Fresh || r.Fresh;
        bool windowsShown = !freshBud && IsHeadsetCurrent(headset, onThisPc, now);
        int? windows = windowsShown ? headset.Percent : null;
        DateTimeOffset? windowsAt = windowsShown ? headset.ReadAt : null;

        return new ShownBattery(l, r, c, windows, windowsAt, GaugeFor(left, right, windows, windowsAt, freshBud, now));
    }

    private static ShownPart Part(PartReading part, DateTimeOffset now) =>
        part.Percent is null || part.ReadAt is null
            ? ShownPart.None
            : new ShownPart(part.Percent, part.Charging, IsFresh(part, now), part.ReadAt);

    // Fresh broadcast first, then Windows' figure while it is current, then the broadcast up to an hour old. The
    // broadcast number is the lower of the buds that have a value no older than an hour: an unknown bud is
    // skipped, and with no bud value there is no number.
    private static GaugeFigure? GaugeFor(
        PartReading left, PartReading right, int? windows, DateTimeOffset? windowsAt, bool freshBud, DateTimeOffset now)
    {
        if (!freshBud && windows is int figure && windowsAt is DateTimeOffset readAt)
        {
            return new GaugeFigure(Math.Clamp(figure, 0, 100), BatterySource.Windows, null, null, false, readAt);
        }

        int? l = IsRecent(left, now) ? left.Percent : null;
        int? r = IsRecent(right, now) ? right.Percent : null;
        if (l is null && r is null)
        {
            return null;
        }

        int lower = Math.Clamp(Math.Min(l ?? int.MaxValue, r ?? int.MaxValue), 0, 100);
        DateTimeOffset oldest = DateTimeOffset.MaxValue;
        if (l is not null && left.ReadAt is { } leftAt)
        {
            oldest = leftAt;
        }

        if (r is not null && right.ReadAt is { } rightAt && rightAt < oldest)
        {
            oldest = rightAt;
        }

        bool charging = (l is not null && left.Charging == true) || (r is not null && right.Charging == true);
        return new GaugeFigure(lower, BatterySource.Broadcast, l, r, charging, oldest);
    }
}
