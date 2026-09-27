namespace Earshot.Widget;

// The owner's rule, applied exactly: his only if model and colour match the claim, the signal clears the
// threshold, and the battery is consistent with his last reading; a live connection to this PC confirms
// outright; short of that, fail closed; unmatched devices are counted and nothing else.
public enum OwnershipVerdict
{
    Owned,                    // every check passed
    OwnedByLiveConnection,    // model, colour and signal passed; battery waived by a live connection to this PC
    NoClaim,
    NotEvaluated,             // the parse was not Ok (truncated, wrong type, unknown form)
    ModelOrColourMismatch,
    SignalBelowThreshold,
    BatteryUnreadable,        // every nibble unknown and no live connection
    BatteryInconsistent,
    AmbiguousCandidates       // live connection, but two senders clear the threshold
}

public sealed record OwnershipInput(
    ProximityParse Parse, sbyte Rssi, WidgetClaim? Claim, ProximityDecodeTable Table,
    bool LiveConnectionToThisPc, int CandidatesClearingThreshold, DateTimeOffset AtUtc);

public sealed record OwnershipResult(OwnershipVerdict Verdict, OwnedBattery? UpdatedLast);

public static class OwnershipRule
{
    public static OwnershipResult Evaluate(OwnershipInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.Parse.Status != ProximityParseStatus.Ok || input.Parse.Message is not ProximityMessage message)
        {
            return new OwnershipResult(OwnershipVerdict.NotEvaluated, null);
        }

        if (input.Claim is not WidgetClaim claim)
        {
            return new OwnershipResult(OwnershipVerdict.NoClaim, null);
        }

        if (message.ModelHigh != claim.ModelHigh || message.ModelLow != claim.ModelLow || message.Colour != claim.Colour)
        {
            return new OwnershipResult(OwnershipVerdict.ModelOrColourMismatch, null);
        }

        if (input.Rssi < claim.SignalThresholdDbm)
        {
            return new OwnershipResult(OwnershipVerdict.SignalBelowThreshold, null);
        }

        bool budsProved = input.Table.HighNibbleIsRight is not null;
        DecodedReading reading = ProximityDecoder.Decode(message, input.Table, input.AtUtc);
        int? wireHigh = KnownNibble((message.BatteryA >> 4) & 0x0F);
        int? wireLow = KnownNibble(message.BatteryA & 0x0F);
        int? currentLeft = budsProved ? PercentToNibble(reading.Left.Percent) : null;
        int? currentRight = budsProved ? PercentToNibble(reading.Right.Percent) : null;
        int? currentCase = PercentToNibble(reading.Case.Percent);

        if (input.LiveConnectionToThisPc)
        {
            if (input.CandidatesClearingThreshold > 1)
            {
                return new OwnershipResult(OwnershipVerdict.AmbiguousCandidates, null);
            }

            OwnedBattery reSynced = BuildUpdatedLast(budsProved, wireHigh, wireLow, currentLeft, currentRight, currentCase, claim.Last, input.AtUtc);
            return new OwnershipResult(OwnershipVerdict.OwnedByLiveConnection, reSynced);
        }

        bool anyBudKnown = budsProved ? currentLeft is not null || currentRight is not null : wireHigh is not null || wireLow is not null;
        if (!anyBudKnown && currentCase is null)
        {
            return new OwnershipResult(OwnershipVerdict.BatteryUnreadable, null);
        }

        bool budsConsistent = budsProved
            ? PartConsistent(currentLeft, claim.Last.NibbleLow, reading.Left.Charging == true) &&
              PartConsistent(currentRight, claim.Last.NibbleHigh, reading.Right.Charging == true)
            : PairConsistentUnordered(wireHigh, wireLow, claim.Last.NibbleHigh, claim.Last.NibbleLow);

        bool caseConsistent = PartConsistent(currentCase, claim.Last.Case, reading.Case.Charging == true);

        if (!budsConsistent || !caseConsistent)
        {
            return new OwnershipResult(OwnershipVerdict.BatteryInconsistent, null);
        }

        OwnedBattery updated = BuildUpdatedLast(budsProved, wireHigh, wireLow, currentLeft, currentRight, currentCase, claim.Last, input.AtUtc);
        return new OwnershipResult(OwnershipVerdict.Owned, updated);
    }

    // A new value is consistent with the last one when there is nothing to compare (either is unknown),
    // when it is equal or lower, or when it is exactly one step higher. Higher by more than one step is
    // consistent only while the part's own charging flag reads true for this message; an unproved charging
    // bit is read as not charging, so a bigger jump then always fails.
    private static bool PartConsistent(int? current, int? last, bool charging)
    {
        if (current is null || last is null)
        {
            return true;
        }

        int delta = current.Value - last.Value;
        return delta <= 1 || charging;
    }

    // While the bud order is unproved the two nibbles are compared as a set, larger against larger and
    // smaller against smaller, with no charging exemption, since no part has a proved charging bit at that
    // point. When one of the current pair is unknown, the known value only has to line up with one of the
    // two last values: which physical bud it was is not known either.
    private static bool PairConsistentUnordered(int? newHigh, int? newLow, int? lastHigh, int? lastLow)
    {
        if (newHigh is null && newLow is null)
        {
            return true;
        }

        if (newHigh is null || newLow is null)
        {
            int known = newHigh ?? newLow!.Value;
            return lastHigh is null && lastLow is null
                || (lastHigh is not null && PartConsistent(known, lastHigh, charging: false))
                || (lastLow is not null && PartConsistent(known, lastLow, charging: false));
        }

        int newBig = Math.Max(newHigh.Value, newLow.Value);
        int newSmall = Math.Min(newHigh.Value, newLow.Value);

        if (lastHigh is null || lastLow is null)
        {
            int? lastKnown = lastHigh ?? lastLow;
            return lastKnown is null
                || PartConsistent(newBig, lastKnown, charging: false)
                || PartConsistent(newSmall, lastKnown, charging: false);
        }

        int lastBig = Math.Max(lastHigh.Value, lastLow.Value);
        int lastSmall = Math.Min(lastHigh.Value, lastLow.Value);
        return PartConsistent(newBig, lastBig, charging: false) && PartConsistent(newSmall, lastSmall, charging: false);
    }

    private static OwnedBattery BuildUpdatedLast(
        bool budsProved, int? wireHigh, int? wireLow, int? left, int? right, int? currentCase, OwnedBattery previous, DateTimeOffset at)
    {
        int? high = budsProved ? right ?? previous.NibbleHigh : wireHigh ?? previous.NibbleHigh;
        int? low = budsProved ? left ?? previous.NibbleLow : wireLow ?? previous.NibbleLow;
        int? updatedCase = currentCase ?? previous.Case;
        return new OwnedBattery(high, low, updatedCase, at);
    }

    private static int? PercentToNibble(int? percent) => percent is int p ? p / 10 : null;

    private static int? KnownNibble(int nibble) => nibble is >= 0 and <= 10 ? nibble : null;
}
