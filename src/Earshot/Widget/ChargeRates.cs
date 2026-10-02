namespace Earshot.Widget;

// How fast a part charges, for ChargeEstimator: a rate learned from the owner's own live readings, or for the buds of one
// model a seed from Apple's published figure, which is off until the owner confirms its wording.
//
// The seed. Apple's tech specs page for AirPods Pro 3 (https://support.apple.com/en-us/125135) is said to read "5 minutes
// in the case provides around 1 hour of listening time", against "up to 8 hours" of listening on one charge with noise
// cancellation. One hour of eight is 12.5% of a full charge in 5 minutes, which is 150 percentage points an hour. That
// figure describes the quick start of a charge from low; a charge slows as it nears full, so a bud estimate seeded from it
// can run ahead of the truth near the top until a learned rate replaces it. The wording could not be fetched from the
// build machine (support.apple.com was not reachable), so it has not been confirmed: UseAppleBudSeed stays false, and no
// bud is estimated until a rate is learned. The owner sets it true after reading the page and finding that wording.
// AppleBudSeedModel is the product id the owner's own pair reports (PairedModel); that it is AirPods Pro 3 is the owner's
// word, not a published fact, and is to be confirmed at the same time. No other model has a seed, and the case never has
// one: the case is estimated only once a charge of it has been seen and a rate learned from it.
//
// A learned rate replaces the seed for its model and part as soon as there is one.
internal static class ChargeRates
{
    // Off until the owner has confirmed Apple's wording (above). The one switch for the seed.
    public const bool UseAppleBudSeed = false;

    // The product id the seed is for (see above).
    public const ushort AppleBudSeedModel = 0x2027;

    // 1 hour of listening out of 8, gained in 5 minutes: (1 / 8) of 100 points every 5 minutes, 12 times an hour.
    public const double AppleBudSeedPercentPerHour = 100.0 / 8.0 * (60.0 / 5.0);

    // The rate a part of the given model charges at, in percentage points an hour, or null when there is none to use.
    public static double? For(LastReadingBook book, ushort model, ChargePart part, bool useAppleBudSeed = UseAppleBudSeed)
    {
        ArgumentNullException.ThrowIfNull(book);
        if (book.RateFor(model, part) is { } learned)
        {
            return learned.PercentPerHour;
        }

        return useAppleBudSeed && part == ChargePart.Bud && model == AppleBudSeedModel ? AppleBudSeedPercentPerHour : null;
    }

    public static ChargePart PartOf(ChargeComponent component) =>
        component == ChargeComponent.Case ? ChargePart.Case : ChargePart.Bud;
}

// Learns a part's charge rate from the owner's own live readings of the linked pair. Design choices, with reasons:
//
// What counts as a sample. The message carries each level in steps of 10%, so the time a value was first read says little
// about the charge on its own; the moment the level is seen to step up does. A sample is therefore taken between two
// steps up of the same part, both seen while it says it is charging, with no reading in between that is lower or not
// charging (either ends the charge as far as can be told):
//   - the first step (the start) counts only when the reading before it was heard at most StartEdgeGap earlier, so its
//     time is known to within that. A start read late would shorten the span and make the rate too fast.
//   - the last step (the end) may follow a gap (the lid shut and opened again): a step read late lengthens the span and
//     makes the rate slower, never faster, and so does a bud that left the case and came back in between.
//   - the end is below 100%: a part reads 100% for as long as it stays full, so the time it got there is not known.
//   - the span is at least MinSpan, so a sample spans more than a step's worth of time, and at most MaxSpan, so a reading
//     of yesterday's charge and one of today's are not taken for one charge.
// The rate is the points between the two steps over the time between them. The estimate it drives therefore errs slow,
// which a newer live reading then corrects, rather than fast.
//
// How rates are kept. One rate per model and part (both buds share one), beside the last readings in the same file
// (LastReadingStore). A new sample replaces the one held: the latest charge says most about a battery that ages, and
// within one charge each later step gives a longer span than the one before. Nothing is averaged and nothing else is kept.
//
// What is in memory only. The run of readings each part is in is held here and nowhere else, so a restart, a new link or
// a new paired model starts each part over.
internal sealed class ChargeRateLearner
{
    // A start step is believed only when the reading before it was heard this recently. Design choice: two minutes is the
    // linked set's own loss limit (BroadcastRules.LostLimit), past which nothing is assumed about it.
    public static readonly TimeSpan StartEdgeGap = TimeSpan.FromMinutes(2);

    // Design choices: a sample spans at least a quarter of an hour, and at most three hours.
    public static readonly TimeSpan MinSpan = TimeSpan.FromMinutes(15);

    public static readonly TimeSpan MaxSpan = TimeSpan.FromHours(3);

    private readonly Run?[] _runs = new Run?[3];

    // Forgets every run: after a restart, a new link or a new paired model nothing said before is the same charge.
    public void Reset() => Array.Clear(_runs);

    // One live reading of one part of the linked pair. Returns a rate when this reading completes a sample.
    public LearnedRate? Observe(ChargeComponent component, ushort model, int percent, bool charging, DateTimeOffset at)
    {
        int i = (int)component;
        if (!charging || _runs[i] is not Run run || at < run.LastAt || percent < run.LastPercent)
        {
            // Not charging ends the run; a first reading, a clock that went back or a lower level starts a new one.
            _runs[i] = charging ? new Run(percent, at, null, null) : null;
            return null;
        }

        if (percent == run.LastPercent)
        {
            _runs[i] = run with { LastAt = at };
            return null;
        }

        // A step up, read at this message.
        bool startKnown = at - run.LastAt <= StartEdgeGap;
        if (run.StartAt is not DateTimeOffset startAt || run.StartPercent is not int startPercent || at - startAt > MaxSpan)
        {
            _runs[i] = new Run(percent, at, startKnown ? percent : null, startKnown ? at : null);
            return null;
        }

        _runs[i] = run with { LastPercent = percent, LastAt = at };
        TimeSpan span = at - startAt;
        if (percent >= 100 || span < MinSpan)
        {
            return null;
        }

        double perHour = (percent - startPercent) / span.TotalHours;
        return new LearnedRate(model, ChargeRates.PartOf(component), perHour, at, span);
    }

    // Where one part's run stands: its last reading, and the step up the run started from, when that is known.
    private readonly record struct Run(int LastPercent, DateTimeOffset LastAt, int? StartPercent, DateTimeOffset? StartAt);
}
