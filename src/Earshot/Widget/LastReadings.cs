namespace Earshot.Widget;

// One of the three parts the message reports a level for.
internal enum ChargeComponent { Left, Right, Case }

// The kind of part a charge rate is for: both buds of one model charge alike, the case on its own.
internal enum ChargePart { Bud, Case }

// The last fresh reading of one part of the linked pair: the value, the charging flag, when it was read and the model
// that sent it. No address, no name and nothing else about the sender is kept.
internal sealed record SavedReading(int Percent, bool Charging, DateTimeOffset ReadAt, ushort Model);

// A charge rate learned from the owner's own live readings (ChargeRateLearner says what counts): the model and part it
// is for, percentage points an hour, when it was measured and over how long.
internal sealed record LearnedRate(ushort Model, ChargePart Part, double PercentPerHour, DateTimeOffset MeasuredAt, TimeSpan Span);

// What is kept of the linked pair across restarts: the last fresh reading of each part and the learned rates. Immutable:
// every change gives a new book, and a reading that changes nothing gives the same one back, so a reference compare
// says whether anything changed. Pure: no clock and no file here (LastReadingStore reads and writes it).
internal sealed record LastReadingBook(SavedReading? Left, SavedReading? Right, SavedReading? Case, IReadOnlyList<LearnedRate> Rates)
{
    public static LastReadingBook Empty { get; } = new(null, null, null, Array.Empty<LearnedRate>());

    public SavedReading? Of(ChargeComponent component) => component switch
    {
        ChargeComponent.Left => Left,
        ChargeComponent.Right => Right,
        _ => Case,
    };

    // The saved reading of a part, only when it was sent by the given model: a pair of another model is not the one
    // the owner has paired now, so nothing it said is shown for it.
    public SavedReading? Of(ChargeComponent component, ushort? model) =>
        model is ushort m && Of(component) is { } saved && saved.Model == m ? saved : null;

    public LearnedRate? RateFor(ushort model, ChargePart part)
    {
        foreach (LearnedRate rate in Rates)
        {
            if (rate.Model == model && rate.Part == part)
            {
                return rate;
            }
        }

        return null;
    }

    // The book with the parts of one fresh reading of the linked pair. A part the message gives no level for (the case
    // while the buds are worn, a bud out of range) keeps what it had.
    public LastReadingBook WithReading(DecodedReading reading, ushort model, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(reading);
        SavedReading? left = Merge(Left, reading.Left, model, at);
        SavedReading? right = Merge(Right, reading.Right, model, at);
        SavedReading? box = Merge(Case, reading.Case, model, at);
        return ReferenceEquals(left, Left) && ReferenceEquals(right, Right) && ReferenceEquals(box, Case)
            ? this
            : this with { Left = left, Right = right, Case = box };
    }

    // The book with a learned rate in place of the one held for the same model and part.
    public LastReadingBook WithRate(LearnedRate rate)
    {
        ArgumentNullException.ThrowIfNull(rate);
        var rates = new List<LearnedRate>(Rates.Count + 1);
        foreach (LearnedRate held in Rates)
        {
            if (held.Model != rate.Model || held.Part != rate.Part)
            {
                rates.Add(held);
            }
        }

        rates.Add(rate);
        return this with { Rates = rates };
    }

    // Whether the two books differ in anything but the read times: a value, a charging flag, a model or a rate. A
    // change of read time alone is written less often (LastReadingStore.ReadTimeWriteInterval).
    public static bool DiffersBeyondReadTimes(LastReadingBook a, LastReadingBook b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return !SameValue(a.Left, b.Left) || !SameValue(a.Right, b.Right) || !SameValue(a.Case, b.Case) ||
            !a.Rates.SequenceEqual(b.Rates);
    }

    private static bool SameValue(SavedReading? a, SavedReading? b) =>
        a is null ? b is null : b is not null && a.Percent == b.Percent && a.Charging == b.Charging && a.Model == b.Model;

    private static SavedReading? Merge(SavedReading? held, PartReading incoming, ushort model, DateTimeOffset at)
    {
        if (incoming.Percent is not int percent)
        {
            return held;
        }

        var saved = new SavedReading(Math.Clamp(percent, 0, 100), incoming.Charging == true, at, model);
        return saved == held ? held : saved;
    }
}
