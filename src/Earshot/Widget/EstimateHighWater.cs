namespace Earshot.Widget;

// The highest estimate shown so far for each part's saved reading, so that an estimate never falls: not when the clock is
// put back between one render and the next, and not after a restart with the clock set back, since the marks are kept
// with the last readings (LastReadingStore). Design choice: the mark is kept per reading, keyed by its read time and
// value, and a newer reading of the part is another reading with no mark of its own, so what a newer reading says
// replaces the estimate even when it is lower (it is a reading, and the estimate was a guess).
//
// BatteryFreshness is the one place a shown value is worked out, and it asks here with each estimate it works out, so
// every surface (the card, the gauge, the alert) is held to what any of them has shown. Thread-safe: the UI thread draws
// and the service saves.
internal sealed class EstimateHighWater
{
    private readonly Lock _gate = new();
    private readonly EstimateMark?[] _marks = new EstimateMark?[3];
    private int _version;

    public EstimateHighWater()
    {
    }

    // Starts from the marks that were saved.
    public EstimateHighWater(IEnumerable<EstimateMark> saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        foreach (EstimateMark mark in saved)
        {
            _marks[(int)mark.Component] ??= mark;
        }
    }

    // Changes each time a mark is raised or replaced, so the service can tell there is something new to write.
    public int Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    // What to show for a part's reading, given the estimate worked out for now (null when there is none to show): the
    // higher of it and the highest shown before for this same reading, which is then recorded. Null when neither exists.
    public int? Observe(ChargeComponent component, DateTimeOffset readAt, int readPercent, int? estimate)
    {
        lock (_gate)
        {
            EstimateMark? held = _marks[(int)component];
            bool same = held is not null && held.ReadAt == readAt && held.ReadPercent == readPercent;
            if (same)
            {
                if (estimate is int higher && higher > held!.Percent)
                {
                    _marks[(int)component] = held with { Percent = higher };
                    _version++;
                    return higher;
                }

                return held!.Percent;
            }

            if (estimate is int first)
            {
                _marks[(int)component] = new EstimateMark(component, readAt, readPercent, first);
                _version++;
            }

            return estimate;
        }
    }

    // The marks held, for the readings given: a mark whose reading is no longer the part's is of no use and is left out.
    public IReadOnlyList<EstimateMark> MarksFor(LastReadingBook book)
    {
        ArgumentNullException.ThrowIfNull(book);
        var kept = new List<EstimateMark>();
        lock (_gate)
        {
            foreach (EstimateMark? mark in _marks)
            {
                if (mark is not null && book.Of(mark.Component) is { } reading && reading.ReadAt == mark.ReadAt && reading.Percent == mark.ReadPercent)
                {
                    kept.Add(mark);
                }
            }
        }

        return kept;
    }
}
