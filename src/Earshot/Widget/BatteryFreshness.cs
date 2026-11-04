namespace Earshot.Widget;

internal enum BatterySource { Broadcast, Windows }

// What a shown value is. Live: a fresh reading of the linked pair heard by this run, within FreshWindow. Last: the last
// reading of the owner's pair, of any age, kept until a newer one is heard (saved across restarts, LastReadingStore). Both
// are readings, in the broadcast's 10% steps; nothing is grown or interpolated. A last reading is drawn in the
// stale style with its age, never as live.
internal enum ReadingKind { Live, Last }

// One part as it is shown: the value, the charging flag of the reading it comes from, what kind of value it is and when
// that reading was read.
internal readonly record struct ShownPart(int? Percent, bool? Charging, ReadingKind Kind, DateTimeOffset? ReadAt)
{
    public static ShownPart None => new(null, null, ReadingKind.Last, null);

    public bool HasValue => Percent is not null;

    // A live value: drawn as current. Anything else is drawn in the stale style with its age.
    public bool Fresh => HasValue && Kind == ReadingKind.Live;
}

// The figure the gauge draws: the number, where it came from and, for the broadcast, the buds it was taken from.
// ReadAt is the oldest read among the buds it is drawn from. Kind is the kind of the bud the number is (the lower one).
internal sealed record GaugeFigure(
    int Percent, BatterySource Source, int? Left, int? Right, bool Charging, DateTimeOffset ReadAt, ReadingKind Kind = ReadingKind.Live);

// What is kept of the owner's pair beyond this run's own readings: the saved last readings and the paired model they are
// shown for.
internal sealed record SavedBattery(LastReadingBook Book, ushort? Model);

// What every surface shows of the battery, worked out once: the card, the gauge and the low battery alert all
// read this, so "what is shown" has one definition. The owner's pair is shown wherever it is: a fresh reading of the
// linked set as live, and otherwise its last reading in the stale style, at any age, until a newer one is heard. Only the
// owner's pair is ever shown: this run's readings only while a set is linked (a case opened near the PC), and saved
// readings only of the paired model, and only fresh readings of the linked set are saved.
//   Left, Right, Case: each part's value, with its kind.
//   WindowsPercent: Windows' own Hands-Free figure, set only when it is what is shown: the AirPods are on this PC, no bud
//   has been live in this connection and the figure is current. It is one figure for the headset, never a bud's or the
//   case's.
//   Gauge: the number the gauge draws while the AirPods are on this PC, or null for the mark alone.
internal sealed record ShownBattery(
    ShownPart Left, ShownPart Right, ShownPart Case, int? WindowsPercent, DateTimeOffset? WindowsReadAt, GaugeFigure? Gauge)
{
    public static ShownBattery None { get; } = new(ShownPart.None, ShownPart.None, ShownPart.None, null, null, null);

    // When the figures that are shown were last read: the newest of the parts', or Windows' figure's time. A part that
    // is old greys by itself and must not make buds that were just heard read as old. Null when nothing is shown.
    public DateTimeOffset? NewestReadAt
    {
        get
        {
            DateTimeOffset? newest = WindowsReadAt;
            foreach (ShownPart part in new[] { Left, Right, Case })
            {
                if (part.ReadAt is DateTimeOffset at && (newest is null || at > newest))
                {
                    newest = at;
                }
            }

            return newest;
        }
    }
}

// How old a battery value may be and still be shown as live, and what is shown of each part. Pure: now is handed in.
internal static class BatteryFreshness
{
    // A value read within this long is live; older, it is a last reading, drawn in the stale style with its age. In use
    // the set sends about 35 documented messages a minute (one every 1.7 s), so 30 s holds about 17; the longest gap from
    // one sender in any capture is 6.14 s and for a merged set 3.11 s. A closed case sends nothing, so values go stale
    // half a minute after the lid closes, which is what the stale style is for.
    public static readonly TimeSpan FreshWindow = TimeSpan.FromSeconds(30);

    // Windows' figure counts only while the AirPods are on this PC and it was read within two poll periods.
    // Nothing says Windows clears it on disconnect, so it is never shown for AirPods that are not here.
    public static readonly TimeSpan HeadsetCurrentWindow = TimeSpan.FromSeconds(120);

    // The gauge's number is the lower of the buds read within this long of the newer of the two, so a bud that crosses
    // FreshWindow while the other is still heard does not drop out of the comparison and come back (the number flipping
    // between buds). Design choice: the value of BroadcastRules.LostLimit, longer than any recorded gap between messages of a
    // set (6.14 s) and the span past which a linked set is taken to be lost. A bud read much longer before the other says
    // nothing of the figure being heard and does not lower it.
    public static readonly TimeSpan GaugeBudHold = BroadcastRules.LostLimit;

    public static bool IsFresh(PartReading part, DateTimeOffset now) => Within(part, now, FreshWindow);

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
        return Shown(
            snapshot.Left, snapshot.Right, snapshot.Case, snapshot.Headset,
            onThisPc: snapshot.Where == AirPodsWhere.ThisPc, linked: snapshot.Selection == BroadcastSelectionState.Linked, now,
            new SavedBattery(snapshot.LastReadings, snapshot.PairedModel));
    }

    // left, right, caseReading: this run's readings of the broadcast. linked: the broadcast's set is the one linked to the
    // owner's pair (a case opened near the PC); this run's readings are shown only then, since an unlinked broadcast may be
    // any pair's. saved: the owner's pair's saved last readings, shown whether or not a set is linked now.
    // onThisPc: the AirPods are connected to this PC; Windows' own figure, which is for the connected headset and needs no
    // link, and the gauge's figure are given only then.
    public static ShownBattery Shown(
        PartReading left, PartReading right, PartReading caseReading, PartReading headset, bool onThisPc, bool linked, DateTimeOffset now,
        SavedBattery? saved = null)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentNullException.ThrowIfNull(caseReading);
        ArgumentNullException.ThrowIfNull(headset);
        if (!linked)
        {
            left = PartReading.Unknown;
            right = PartReading.Unknown;
            caseReading = PartReading.Unknown;
        }

        ShownPart l = Part(left, ChargeComponent.Left, saved, now);
        ShownPart r = Part(right, ChargeComponent.Right, saved, now);
        ShownPart c = Part(caseReading, ChargeComponent.Case, saved, now);

        bool freshBud = l.Fresh || r.Fresh;
        bool windowsShown = !freshBud && IsHeadsetCurrent(headset, onThisPc, now);
        int? windows = windowsShown ? headset.Percent : null;
        DateTimeOffset? windowsAt = windowsShown ? headset.ReadAt : null;

        GaugeFigure? gauge = onThisPc ? GaugeFor(l, r, windows, windowsAt) : null;
        return new ShownBattery(l, r, c, windows, windowsAt, gauge);
    }

    // One part: this run's reading while it is fresh; otherwise the newer of this run's reading and the saved one, as a
    // last reading. A saved reading is never live: it was not heard by this run.
    private static ShownPart Part(PartReading live, ChargeComponent component, SavedBattery? saved, DateTimeOffset now)
    {
        bool liveHas = live.Percent is not null && live.ReadAt is not null;
        if (liveHas && IsFresh(live, now))
        {
            return new ShownPart(live.Percent, live.Charging, ReadingKind.Live, live.ReadAt);
        }

        SavedReading? kept = saved?.Book.Of(component, saved.Model);
        int percent;
        bool? charging;
        DateTimeOffset readAt;
        if (liveHas && (kept is null || live.ReadAt >= kept.ReadAt))
        {
            percent = live.Percent!.Value;
            charging = live.Charging;
            readAt = live.ReadAt!.Value;
        }
        else if (kept is not null)
        {
            percent = kept.Percent;
            charging = kept.Charging;
            readAt = kept.ReadAt;
        }
        else
        {
            return ShownPart.None;
        }

        return new ShownPart(Math.Clamp(percent, 0, 100), charging, ReadingKind.Last, readAt);
    }

    // Windows' figure first, while no bud has been live in this connection and it is current; then the buds. The broadcast
    // number is the lower of the buds that have a value and were read within GaugeBudHold of the newer of the two: an unknown
    // bud is skipped, and with no bud value there is no number. Of two buds at the same value the live one, then the fresher,
    // is the number's. It is a live figure only when the bud it is taken from is live.
    private static GaugeFigure? GaugeFor(ShownPart left, ShownPart right, int? windows, DateTimeOffset? windowsAt)
    {
        if (windows is int figure && windowsAt is DateTimeOffset readAt)
        {
            return new GaugeFigure(Math.Clamp(figure, 0, 100), BatterySource.Windows, null, null, false, readAt);
        }

        if (!left.HasValue && !right.HasValue)
        {
            return null;
        }

        DateTimeOffset newest = NewestBudRead(left, right);
        left = Held(left, newest);
        right = Held(right, newest);

        ShownPart lower = !right.HasValue ? left
            : !left.HasValue ? right
            : left.Percent < right.Percent ? left
            : right.Percent < left.Percent ? right
            : Rank(left.Kind) < Rank(right.Kind) ? left
            : Rank(right.Kind) < Rank(left.Kind) ? right
            : left.ReadAt >= right.ReadAt ? left : right;

        DateTimeOffset oldest = DateTimeOffset.MaxValue;
        foreach (ShownPart bud in new[] { left, right })
        {
            if (bud.HasValue && bud.ReadAt is DateTimeOffset at && at < oldest)
            {
                oldest = at;
            }
        }

        bool charging = left.Charging == true || right.Charging == true;
        return new GaugeFigure(Math.Clamp(lower.Percent!.Value, 0, 100), BatterySource.Broadcast, left.Percent, right.Percent, charging, oldest, lower.Kind);
    }

    private static DateTimeOffset NewestBudRead(ShownPart left, ShownPart right)
    {
        DateTimeOffset newest = DateTimeOffset.MinValue;
        foreach (ShownPart bud in new[] { left, right })
        {
            if (bud.HasValue && bud.ReadAt is DateTimeOffset at && at > newest)
            {
                newest = at;
            }
        }

        return newest;
    }

    // The bud when it was read within GaugeBudHold of the newer bud's read time, otherwise nothing.
    private static ShownPart Held(ShownPart bud, DateTimeOffset newest) =>
        bud.HasValue && bud.ReadAt is DateTimeOffset at && newest - at <= GaugeBudHold ? bud : ShownPart.None;

    private static int Rank(ReadingKind kind) => kind == ReadingKind.Live ? 0 : 1;
}
