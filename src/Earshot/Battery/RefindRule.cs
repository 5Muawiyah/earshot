
namespace Earshot.Widget;

// One documented-form message of the paired model and the remembered colour, as the re-find rule reads it. The tag is the
// run's own name for the sender, kept in memory only, so the caller can leave out the senders of a set it still follows.
internal readonly record struct RefindSample(uint Tag, DateTimeOffset At, sbyte Rssi, ProximityMessage Message);

// What the rule made of the messages it was given.
//   Refound:      the remembered pair is the one sending.
//   NotEnough:    too few messages, or too short a span, to say anything yet. Not a refusal: more may come.
//   Ambiguous:    two different field sets of the model and colour in the window.
//   Weak:         the median signal is under the near-PC level.
//   Inconsistent: the levels cannot have come from the last reading in the time that has passed (or there is no last
//                 reading to judge by).
//   Expired:      the link was last heard longer ago than BroadcastRules.RememberLimit.
internal enum RefindVerdict { Refound, NotEnough, Ambiguous, Weak, Inconsistent, Expired }

// Decides whether a pair heard with its case level unknown (worn, out of the case) is the pair the owner linked by opening
// the case next to the PC, with nothing but what the broadcast says and what was last read. A case open still links by its
// own rule and always wins, so the owner corrects a wrong decision here by opening the case.
//
// Who may pass. All of these must hold over the last BroadcastRules.RefindWindow:
//   - every message says one set's fields: the same two bud levels as an unordered pair, and one case level among those
//     that give one (a case level comes and goes as the buds leave the case and return). Two different field sets of the
//     model and colour in the window: nobody is found, since the owner's cannot be told from the other;
//   - at least BroadcastRules.RefindMessages messages, the first and last at least BroadcastRules.RefindSpan apart, with
//     their median at least BroadcastRules.LinkThresholdDbm: next to the PC, the bar a link asks;
//   - the levels can be the saved last reading's, part by part, with the buds as an unordered pair (which side is which is
//     unproved). With dt the time since that part was read:
//       a bud at most one step above its last reading, or, if it was charging then or is charging now, at most one step
//       above it plus what the bud's rate gives in dt, capped at 100;
//       a bud at most one step plus BroadcastRules.DischargePerHour times dt below it;
//       the case, when both give a level, at most one step above unless either says it is charging, and not bounded below,
//       since it falls by what it gives the buds;
//   - the link was last heard no longer ago than BroadcastRules.RememberLimit.
// A stranger's pair of the same model and colour passes only if it is alone near the PC and says levels the owner's last
// reading allows, which is the same-model risk the owner accepted, now for hours rather than seconds. New buds in place
// of the owner's say levels above the last reading without charging, and are refused until a case open links them.
//
// Pure: no clock, no file, no state. The caller hands in the window, the memory, the saved book and the time.
internal static class RefindRule
{
    public static RefindVerdict Judge(
        IReadOnlyList<RefindSample> window, LinkMemory memory, LastReadingBook book, ProximityDecodeTable table, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(table);

        if (now - memory.LastHeardAt > BroadcastRules.RememberLimit)
        {
            return RefindVerdict.Expired;
        }

        var samples = window
            .Where(s => s.Message.Model == memory.Model && s.Message.Colour == memory.Colour && now - s.At <= BroadcastRules.RefindWindow)
            .ToList();
        if (samples.Count < BroadcastRules.RefindMessages ||
            samples.Max(s => s.At) - samples.Min(s => s.At) < BroadcastRules.RefindSpan)
        {
            return RefindVerdict.NotEnough;
        }

        if (!OneFieldSet(samples))
        {
            return RefindVerdict.Ambiguous;
        }

        if (Median(samples.Select(s => (double)s.Rssi).ToList()) < BroadcastRules.LinkThresholdDbm)
        {
            return RefindVerdict.Weak;
        }

        return Consistent(samples, memory.Model, book, table) ? RefindVerdict.Refound : RefindVerdict.Inconsistent;
    }

    // Every message continues every other (BroadcastSenderSets.Continues): the same two bud levels as an unordered pair,
    // and the same case level wherever two both give one. That is the same as all the bud pairs being equal and the known
    // case levels being one value, which is checked in one pass rather than pairwise.
    private static bool OneFieldSet(List<RefindSample> samples)
    {
        (int Low, int High)? pair = null;
        int? caseNibble = null;
        foreach (RefindSample sample in samples)
        {
            int high = (sample.Message.BatteryA >> 4) & 0x0F;
            int low = sample.Message.BatteryA & 0x0F;
            (int Low, int High) budPair = (Math.Min(high, low), Math.Max(high, low));
            if (pair is { } held && held != budPair)
            {
                return false;
            }

            pair = budPair;
            if (BroadcastSenderSets.CaseKnown(sample.Message))
            {
                int nibble = sample.Message.BatteryB & 0x0F;
                if (caseNibble is int heldCase && heldCase != nibble)
                {
                    return false;
                }

                caseNibble = nibble;
            }
        }

        return true;
    }

    private static bool Consistent(List<RefindSample> samples, ushort model, LastReadingBook book, ProximityDecodeTable table)
    {
        // Both buds' last readings are needed to judge a pair of buds; without them there is no basis, and nobody passes.
        if (book.Of(ChargeComponent.Left, model) is not { } savedLeft || book.Of(ChargeComponent.Right, model) is not { } savedRight)
        {
            return false;
        }

        RefindSample newest = samples.MaxBy(s => s.At);
        DecodedReading reading = ProximityDecoder.Decode(newest.Message, table, newest.At);
        double rate = ChargeRates.For(book, model, ChargePart.Bud, useAppleBudSeed: true) ?? ChargeRates.AppleBudSeedPercentPerHour;

        PartReading[] now = [reading.Left, reading.Right];
        SavedReading[] saved = [savedLeft, savedRight];
        if (!now.Any(p => p.Percent is not null))
        {
            return false;
        }

        // Which side is which is unproved, so the buds match either way round.
        bool budsFit = BudsFit(now, saved, [0, 1], newest.At, rate) || BudsFit(now, saved, [1, 0], newest.At, rate);
        return budsFit && CaseFits(samples, model, book, table);
    }

    private static bool BudsFit(PartReading[] now, SavedReading[] saved, int[] order, DateTimeOffset at, double rate)
    {
        for (int i = 0; i < now.Length; i++)
        {
            if (now[i].Percent is int percent && !BudFits(percent, now[i].Charging == true, saved[order[i]], at, rate))
            {
                return false;
            }
        }

        return true;
    }

    private static bool BudFits(int percent, bool charging, SavedReading saved, DateTimeOffset at, double rate)
    {
        double hours = Math.Max(0, (at - saved.ReadAt).TotalHours);
        double up = saved.Percent + BroadcastRules.StepAllowance;
        if (saved.Charging || charging)
        {
            up = Math.Min(100, up + rate * hours);
        }

        double down = saved.Percent - (BroadcastRules.StepAllowance + BroadcastRules.DischargePerHour * hours);
        return percent <= up && percent >= down;
    }

    // The case level, from the newest message that gives one, against the saved case reading. Nothing to compare when
    // either has no level.
    private static bool CaseFits(List<RefindSample> samples, ushort model, LastReadingBook book, ProximityDecodeTable table)
    {
        if (book.Of(ChargeComponent.Case, model) is not { } saved)
        {
            return true;
        }

        foreach (RefindSample sample in samples.OrderByDescending(s => s.At))
        {
            PartReading caseReading = ProximityDecoder.Decode(sample.Message, table, sample.At).Case;
            if (caseReading.Percent is int percent)
            {
                return saved.Charging || caseReading.Charging == true || percent <= saved.Percent + BroadcastRules.StepAllowance;
            }
        }

        return true;
    }

    private static double Median(List<double> values)
    {
        double[] sorted = values.Order().ToArray();
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
