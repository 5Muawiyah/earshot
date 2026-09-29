using System.Globalization;

namespace Earshot.Widget;

public enum DecodeField { HighNibbleIsRight, FlipBit, CaseNibble, CaseChargingBit, RightChargingBit, LeftChargingBit, LeftInEarBit, RightInEarBit, Lid }

public enum FieldProofStatus { Unproved, Proved, Withdrawn, NotProvableBySetup }

public sealed record FieldProof(FieldProofStatus Status, int Agree, int Disagree, string? Detail);

public sealed record DecodeProofResult(
    ProximityDecodeTable Table,
    IReadOnlyDictionary<DecodeField, FieldProof> Fields,
    IReadOnlyList<string> Notes);

// Whether one record tells the two buds apart, and if so which nibble it says is the right bud.
public sealed record BudOrderEvidence(bool? HighNibbleIsRight, string? WhyNot);

// Works out, from the owner's own set-up records alone, which parts of the documented message can be read.
// Pure: no clock, no I/O. Nothing here is a fact about the device that was not seen in a record, and the
// owner's picks are the only reference a record has, so every rule needs its evidence twice over before it
// proves anything, and a later record that disagrees withdraws what an earlier pair proved.
public static class DecodeProof
{
    // Percent: one nibble step either way. How the iPhone rounds the figure is not established, and the owner
    // rounds it to a 10 step himself; between the two roundings the gap to a nibble is at most one step.
    public const int Tolerance = 10;

    // Why two: one discriminating record is already strong, but it cannot rule out the owner entering the two
    // buds the wrong way round. Two independent entries both wrong the same way is a much smaller risk. The case
    // nibble needs the same two records that agree with the owner's pick, for the same reason.
    public const int OrderRecordsNeeded = 2;

    // A status bit that happens to be constant across a few records fits trivially, so a flip needs four
    // discriminating records with each value of the bit in at least two of them.
    public const int FlipRecordsNeeded = 4;

    private const int ChargingLowBit = 4;
    private const int ChargingHighBit = 7;

    public static DecodeProofResult Evaluate(IReadOnlyList<BatterySetupRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var usable = new List<(BatterySetupRecord Record, ProximityMessage Message)>();
        foreach (BatterySetupRecord record in records)
        {
            if (record.Candidate?.LastOkMessage is ProximityMessage message && record.Picks is not null)
            {
                usable.Add((record, message));
            }
        }

        var notes = new List<string>();
        var fields = new Dictionary<DecodeField, FieldProof>();

        // Bud order and flip.
        var discriminating = new List<(bool HighIsRight, byte Status)>();
        foreach ((BatterySetupRecord record, ProximityMessage message) in usable)
        {
            BudOrderEvidence evidence = Discriminate(record);
            if (evidence.HighNibbleIsRight is bool highIsRight)
            {
                discriminating.Add((highIsRight, message.Status));
            }
            else
            {
                notes.Add("record " + record.FileName + ": " + evidence.WhyNot + ", not discriminating");
            }
        }

        bool? highNibbleIsRight = null;
        int? flipBit = null;
        bool flipWhenSet = false;
        FieldProof orderProof;
        FieldProof flipProof = new(FieldProofStatus.Unproved, 0, 0, null);

        int toRight = discriminating.Count(d => d.HighIsRight);
        int toLeft = discriminating.Count - toRight;
        if (discriminating.Count >= OrderRecordsNeeded && (toRight == 0 || toLeft == 0))
        {
            highNibbleIsRight = toRight > 0;
            orderProof = new FieldProof(FieldProofStatus.Proved, discriminating.Count, 0, null);
        }
        else if (discriminating.Count >= OrderRecordsNeeded)
        {
            orderProof = new FieldProof(FieldProofStatus.Withdrawn, Math.Max(toRight, toLeft), Math.Min(toRight, toLeft), null);
            (bool found, int bit, bool whenSet, int fitting) = FindFlip(discriminating);
            if (discriminating.Count >= FlipRecordsNeeded)
            {
                if (found)
                {
                    highNibbleIsRight = true;
                    flipBit = bit;
                    flipWhenSet = whenSet;
                    orderProof = new FieldProof(FieldProofStatus.Proved, discriminating.Count, 0, "the order follows a status bit");
                    flipProof = new FieldProof(FieldProofStatus.Proved, discriminating.Count, 0, null);
                }
                else
                {
                    notes.Add("bud order: " + fitting.ToString(CultureInfo.InvariantCulture) + " status bits explain the records, so no flip is proved");
                }
            }
        }
        else
        {
            orderProof = new FieldProof(FieldProofStatus.Unproved, discriminating.Count, 0, null);
        }

        fields[DecodeField.HighNibbleIsRight] = orderProof;
        fields[DecodeField.FlipBit] = flipProof;

        // The case nibble: proved the way a bud is, by agreement with the owner's picks and never before. Two records
        // whose case nibble is within Tolerance of the owner's case pick prove it (the count the bud order needs), and
        // a record that does not agree withdraws it, as a record that contradicts the order withdraws the order.
        int caseAgree = 0;
        int caseDisagree = 0;
        foreach ((BatterySetupRecord record, ProximityMessage message) in usable)
        {
            if (BatteryNibble.ToPercent(message.BatteryB & 0x0F) is int percent)
            {
                if (Math.Abs(percent - record.Picks.Case) <= Tolerance)
                {
                    caseAgree++;
                }
                else
                {
                    caseDisagree++;
                }
            }
        }

        bool caseProved = caseAgree >= OrderRecordsNeeded && caseDisagree == 0;
        FieldProofStatus caseStatus = caseProved
            ? FieldProofStatus.Proved
            : caseAgree >= OrderRecordsNeeded ? FieldProofStatus.Withdrawn : FieldProofStatus.Unproved;
        fields[DecodeField.CaseNibble] = new FieldProof(caseStatus, caseAgree, caseDisagree, null);

        // The charging bits.
        int? caseBit = ProveChargingBit(usable, part: 2);
        int? rightBit = ProveChargingBit(usable, part: 1);
        int? leftBit = ProveChargingBit(usable, part: 0);
        fields[DecodeField.CaseChargingBit] = ChargingProof(caseBit, usable.Count);
        fields[DecodeField.RightChargingBit] = ChargingProof(rightBit, usable.Count);
        fields[DecodeField.LeftChargingBit] = ChargingProof(leftBit, usable.Count);

        // Three percentage pickers carry no truth about ears or the lid.
        var notProvable = new FieldProof(FieldProofStatus.NotProvableBySetup, 0, 0, null);
        fields[DecodeField.LeftInEarBit] = notProvable;
        fields[DecodeField.RightInEarBit] = notProvable;
        fields[DecodeField.Lid] = notProvable;

        var table = new ProximityDecodeTable(
            highNibbleIsRight, flipBit, flipWhenSet, caseBit, rightBit, leftBit,
            LeftInEarBit: null, RightInEarBit: null, InEarWhenSet: false,
            LidOpenBit: null, LidCounterMask: null, CaseNibbleReadsOnlyWithLidOpen: null,
            CaseNibbleProved: caseProved);
        return new DecodeProofResult(table, fields, notes);
    }

    // True when the record's two bud nibbles match exactly one pick each and different buds; then the
    // evidence says which nibble is the right bud. Otherwise the reason it says nothing.
    public static BudOrderEvidence Discriminate(BatterySetupRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Candidate?.LastOkMessage is not ProximityMessage message)
        {
            return new BudOrderEvidence(null, "no documented-form message");
        }

        int high = (message.BatteryA >> 4) & 0x0F;
        int low = message.BatteryA & 0x0F;
        if (BatteryNibble.ToPercent(high) is null || BatteryNibble.ToPercent(low) is null)
        {
            return new BudOrderEvidence(null, "a bud nibble is unknown");
        }

        if (high == low)
        {
            return new BudOrderEvidence(null, "both buds read the same");
        }

        bool highLeft = Matches(high, record.Picks.Left);
        bool highRight = Matches(high, record.Picks.Right);
        bool lowLeft = Matches(low, record.Picks.Left);
        bool lowRight = Matches(low, record.Picks.Right);
        if ((highLeft && highRight) || (lowLeft && lowRight))
        {
            return new BudOrderEvidence(null, "a nibble matches both picks");
        }

        if ((!highLeft && !highRight) || (!lowLeft && !lowRight))
        {
            return new BudOrderEvidence(null, "no pick matches");
        }

        if (highRight == lowRight)
        {
            return new BudOrderEvidence(null, "both nibbles match the same bud");
        }

        return new BudOrderEvidence(highRight, null);
    }

    private static bool Matches(int nibble, int pick) =>
        BatteryNibble.ToPercent(nibble) is int percent && Math.Abs(percent - pick) <= Tolerance;

    // Every rule (H, bit, when) with H taken as true: predicted(record) = H xor (bit reads when). The rule
    // (not H, bit, not when) is the same rule and is counted once. A rule fits when it predicts every
    // discriminating record and at least two records have the bit set and two have it clear. Exactly one
    // fitting rule proves the flip.
    private static (bool Found, int Bit, bool WhenSet, int Fitting) FindFlip(List<(bool HighIsRight, byte Status)> records)
    {
        int fitting = 0;
        int foundBit = 0;
        bool foundWhen = false;
        for (int bit = 0; bit < 8; bit++)
        {
            int setCount = records.Count(r => ((r.Status >> bit) & 1) == 1);
            if (setCount < 2 || records.Count - setCount < 2)
            {
                continue;
            }

            foreach (bool whenSet in new[] { true, false })
            {
                bool fits = records.All(r => (true ^ ((((r.Status >> bit) & 1) == 1) == whenSet)) == r.HighIsRight);
                if (fits)
                {
                    fitting++;
                    foundBit = bit;
                    foundWhen = whenSet;
                }
            }
        }

        return (fitting == 1, foundBit, foundWhen, fitting);
    }

    // A bit of BatteryB (bits 4 to 7; the low nibble is the case level) proves part P's charging flag when it
    // equals the owner's flag in every record, the flag varies, no other part's flag varies the same way and
    // no other bit varies the same way. Without variation on the pick side an always-on bit would "explain" a
    // part that is always charging, and without variation on the bit side two bits are indistinguishable.
    private static int? ProveChargingBit(List<(BatterySetupRecord Record, ProximityMessage Message)> usable, int part)
    {
        if (usable.Count == 0)
        {
            return null;
        }

        for (int bit = ChargingLowBit; bit <= ChargingHighBit; bit++)
        {
            bool Flag(int p, (BatterySetupRecord Record, ProximityMessage Message) r) => p switch
            {
                0 => r.Record.Picks.LeftCharging,
                1 => r.Record.Picks.RightCharging,
                _ => r.Record.Picks.CaseCharging,
            };

            bool Bit(int b, (BatterySetupRecord Record, ProximityMessage Message) r) => ((r.Message.BatteryB >> b) & 1) == 1;

            if (!usable.All(r => Bit(bit, r) == Flag(part, r)))
            {
                continue;
            }

            if (!usable.Any(r => Flag(part, r)) || !usable.Any(r => !Flag(part, r)))
            {
                continue;
            }

            bool otherPartExplains = false;
            for (int other = 0; other < 3; other++)
            {
                if (other != part && usable.All(r => Flag(other, r) == Flag(part, r)))
                {
                    otherPartExplains = true;
                }
            }

            if (otherPartExplains)
            {
                continue;
            }

            bool otherBitExplains = false;
            for (int other = ChargingLowBit; other <= ChargingHighBit; other++)
            {
                if (other != bit && usable.All(r => Bit(other, r) == Bit(bit, r)))
                {
                    otherBitExplains = true;
                }
            }

            if (otherBitExplains)
            {
                continue;
            }

            return bit;
        }

        return null;
    }

    private static FieldProof ChargingProof(int? bit, int records) =>
        bit is null
            ? new FieldProof(FieldProofStatus.Unproved, 0, 0, null)
            : new FieldProof(FieldProofStatus.Proved, records, 0, null);
}
