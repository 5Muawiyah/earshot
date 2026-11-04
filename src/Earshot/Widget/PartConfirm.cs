namespace Earshot.Widget;

// What one message did to a part: nothing to say about it, took its first value, repeated the held value, started waiting for
// a second message, confirmed a new value, threw out a waiting value (a stray), or threw one out again within the window of the
// last time (two senders that disagree).
internal enum ConfirmOutcome { NoValue, First, Repeated, Candidate, Confirmed, Discarded, Alternating }

// What is held for one part and what is waiting to be confirmed. LastDisputeAt is when a waiting value was last thrown out.
internal readonly record struct PartConfirmState(PartReading Held, PartReading? Pending, DateTimeOffset? LastDisputeAt)
{
    public static PartConfirmState Empty => new(PartReading.Unknown, null, null);
}

internal readonly record struct PartConfirmStep(PartConfirmState State, ConfirmOutcome Outcome);

// One reading per part. A part with no held value takes the first reading at once. A part with a held value takes a new value
// or charging flag only when two messages in a row for that part say the same new thing within BroadcastRules.Window; the
// read time of the held value moves on with every message that repeats it. So one stray value changes nothing, and two
// senders that alternate leave the last confirmed value. Two is the smallest count that is not one message (a design choice).
// The ear state is not a reading: it follows every message. Pure: the time is the message's own read time.
internal static class PartConfirm
{
    public static PartConfirmStep Step(PartConfirmState state, PartReading incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (incoming.Percent is null || incoming.ReadAt is not { } at)
        {
            return new PartConfirmStep(state, ConfirmOutcome.NoValue);
        }

        PartReading held = state.Held;
        if (held.Percent is null)
        {
            return new PartConfirmStep(new PartConfirmState(incoming, null, null), ConfirmOutcome.First);
        }

        bool hasWaiting = state.Pending is not null;
        if (SameThing(held, incoming))
        {
            PartReading moved = held with { ReadAt = at, InEar = incoming.InEar };
            if (!hasWaiting)
            {
                return new PartConfirmStep(state with { Held = moved }, ConfirmOutcome.Repeated);
            }

            return Disputed(state with { Held = moved, Pending = null }, at);
        }

        PartReading heldNow = held with { InEar = incoming.InEar };
        if (state.Pending is { } waiting)
        {
            if (SameThing(waiting, incoming))
            {
                if (waiting.ReadAt is { } waitingAt && at - waitingAt <= BroadcastRules.Window)
                {
                    return new PartConfirmStep(new PartConfirmState(incoming, null, state.LastDisputeAt), ConfirmOutcome.Confirmed);
                }

                // The first of the pair is too old to count: this one starts the wait again.
                return new PartConfirmStep(state with { Held = heldNow, Pending = incoming }, ConfirmOutcome.Candidate);
            }

            // A third thing while one waits: the one that waited was a stray.
            return Disputed(state with { Held = heldNow, Pending = incoming }, at);
        }

        return new PartConfirmStep(state with { Held = heldNow, Pending = incoming }, ConfirmOutcome.Candidate);
    }

    private static PartConfirmStep Disputed(PartConfirmState state, DateTimeOffset at)
    {
        bool again = state.LastDisputeAt is { } last && at - last <= BroadcastRules.Window;
        return new PartConfirmStep(state with { LastDisputeAt = at }, again ? ConfirmOutcome.Alternating : ConfirmOutcome.Discarded);
    }

    private static bool SameThing(PartReading a, PartReading b) => a.Percent == b.Percent && a.Charging == b.Charging;
}
