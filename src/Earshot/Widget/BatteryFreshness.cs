namespace Earshot.Widget;

internal enum BatterySource { Broadcast, Windows }

// What a shown value is. Live: a fresh reading of the linked pair heard by this run, within FreshWindow. Last: the last
// reading of the owner's pair, of any age, kept until a newer one is heard (saved across restarts, LastReadingStore).
// Estimated: a last reading of a part that was charging, grown at its rate (ChargeEstimator); it is drawn with "≈" before
// it and the age of the reading it grew from. Last and Estimated are drawn in the stale style, never as live.
internal enum ReadingKind { Live, Last, Estimated }

// One part as it is shown: the value, the charging flag of the reading it comes from, what kind of value it is and when
// that reading was read. For an estimate, ReadPercent is the reading it grew from.
internal readonly record struct ShownPart(int? Percent, bool? Charging, ReadingKind Kind, DateTimeOffset? ReadAt)
{
    public static ShownPart None => new(null, null, ReadingKind.Last, null);

    public bool HasValue => Percent is not null;

    // A live value: drawn as current. Anything else is drawn in the stale style with its age.
    public bool Fresh => HasValue && Kind == ReadingKind.Live;

    public bool Estimated => HasValue && Kind == ReadingKind.Estimated;

    public int? ReadPercent { get; init; }
}

// The figure the gauge draws: the number, where it came from and, for the broadcast, the buds it was taken from.
// ReadAt is the oldest read among the parts it is drawn from, or for an estimate the reading the number grew from. Kind
// is the kind of the bud the number is (the lower one).
internal sealed record GaugeFigure(
    int Percent, BatterySource Source, int? Left, int? Right, bool Charging, DateTimeOffset ReadAt, ReadingKind Kind = ReadingKind.Live)
{
    public bool LeftEstimated { get; init; }

    public bool RightEstimated { get; init; }
}

// What is kept of the owner's pair beyond this run's own readings: the saved last readings and learned rates, the paired
// model they are shown for, the latest time the estimate has been worked out at (an estimate is never worked out for an
// earlier time than that, so a clock that is put back never makes it fall), and the highest estimate shown for each
// reading (EstimateHighWater: with none given, an estimate is worked out afresh each time).
internal sealed record SavedBattery(
    LastReadingBook Book, ushort? Model, DateTimeOffset? EstimateClock, bool UseAppleBudSeed = ChargeRates.UseAppleBudSeed,
    EstimateHighWater? HighWater = null);

// What every surface shows of the battery, worked out once: the card, the gauge and the low battery alert all
// read this, so "what is shown" has one definition. The owner's pair is shown wherever it is: a fresh reading of the
// linked set as live, and otherwise its last reading (or the estimate grown from it) in the stale style, at any age,
// until a newer one is heard. Only the owner's pair is ever shown: this run's readings only while a set is linked (a case
// opened near the PC), and saved readings only of the paired model, and only fresh readings of the linked set are saved.
//   Left, Right, Case: each part's value, with its kind.
//   WindowsPercent: Windows' own Hands-Free figure, set only when it is what is shown: the AirPods are on this PC, no bud
//   has a live broadcast value and the figure is current. It is one figure for the headset, never a bud's or the case's.
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
            new SavedBattery(snapshot.LastReadings, snapshot.PairedModel, snapshot.EstimateClock, HighWater: snapshot.HighWater));
    }

    // left, right, caseReading: this run's readings of the broadcast. linked: the broadcast's set is the one linked to the
    // owner's pair (a case opened near the PC); this run's readings are shown only then, since an unlinked broadcast may be
    // any pair's. saved: the owner's pair's saved last readings and rates, shown whether or not a set is linked now.
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

        GaugeFigure? gauge = onThisPc ? GaugeFor(l, r, windows, windowsAt, freshBud) : null;
        return new ShownBattery(l, r, c, windows, windowsAt, gauge);
    }

    // One part: this run's reading while it is fresh; otherwise the newer of this run's reading and the saved one, as a
    // last reading, or grown as an estimate when the part was charging and a rate is known. A saved reading is never live:
    // it was not heard by this run.
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

        percent = Math.Clamp(percent, 0, 100);
        double? rate = saved is { Model: ushort model }
            ? ChargeRates.For(saved.Book, model, ChargeRates.PartOf(component), saved.UseAppleBudSeed)
            : null;
        DateTimeOffset estimateAt = saved?.EstimateClock is DateTimeOffset latest && latest > now ? latest : now;
        int? estimate = ChargeEstimator.Estimate(percent, charging == true, readAt, rate, estimateAt);

        // An estimate that has been shown for this reading is not shown lower, whatever the clock has done since.
        if (charging == true && percent < 100 && saved?.HighWater is { } highWater)
        {
            estimate = highWater.Observe(component, readAt, percent, estimate);
        }

        return estimate is int grown
            ? new ShownPart(grown, charging, ReadingKind.Estimated, readAt) { ReadPercent = percent }
            : new ShownPart(percent, charging, ReadingKind.Last, readAt);
    }

    // A live broadcast bud first, then Windows' figure while it is current, then the buds' last readings or estimates at
    // any age. The broadcast number is the lower of the buds that have a value: an unknown bud is skipped, and with no bud
    // value there is no number. Of two buds at the same value the live one, then the read one, is the number's. While any
    // bud is live the number is the lower of the live buds only: another bud's old reading or estimate says nothing about
    // now, and must not lower (or stand beside) a figure that is being heard.
    private static GaugeFigure? GaugeFor(ShownPart left, ShownPart right, int? windows, DateTimeOffset? windowsAt, bool freshBud)
    {
        if (!freshBud && windows is int figure && windowsAt is DateTimeOffset readAt)
        {
            return new GaugeFigure(Math.Clamp(figure, 0, 100), BatterySource.Windows, null, null, false, readAt);
        }

        if (freshBud)
        {
            left = left.Fresh ? left : ShownPart.None;
            right = right.Fresh ? right : ShownPart.None;
        }

        if (!left.HasValue && !right.HasValue)
        {
            return null;
        }

        ShownPart lower = !right.HasValue ? left
            : !left.HasValue ? right
            : left.Percent < right.Percent ? left
            : right.Percent < left.Percent ? right
            : Rank(left.Kind) <= Rank(right.Kind) ? left : right;

        DateTimeOffset oldest = DateTimeOffset.MaxValue;
        foreach (ShownPart bud in new[] { left, right })
        {
            if (bud.HasValue && bud.ReadAt is DateTimeOffset at && at < oldest)
            {
                oldest = at;
            }
        }

        DateTimeOffset shownAt = lower.Kind == ReadingKind.Estimated && lower.ReadAt is DateTimeOffset grownFrom ? grownFrom : oldest;
        bool charging = left.Charging == true || right.Charging == true;
        return new GaugeFigure(Math.Clamp(lower.Percent!.Value, 0, 100), BatterySource.Broadcast, left.Percent, right.Percent, charging, shownAt, lower.Kind)
        {
            LeftEstimated = left.Estimated,
            RightEstimated = right.Estimated,
        };
    }

    private static int Rank(ReadingKind kind) => kind switch
    {
        ReadingKind.Live => 0,
        ReadingKind.Last => 1,
        _ => 2,
    };
}
