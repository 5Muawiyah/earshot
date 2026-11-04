using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// One reading per part: a new value or charging flag is taken only when two messages in a row for that part say the same
// new thing within the window. One stray value changes nothing; two senders that disagree leave the last confirmed value.
[TestClass]
public sealed class PartConfirmTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static PartReading Msg(int percent, double seconds, bool? charging = false, bool? inEar = null) =>
        new(percent, charging, inEar) { ReadAt = Epoch + TimeSpan.FromSeconds(seconds) };

    private static PartConfirmState Feed(PartConfirmState state, params PartReading[] messages)
    {
        foreach (PartReading message in messages)
        {
            state = PartConfirm.Step(state, message).State;
        }

        return state;
    }

    [TestMethod]
    public void TheFirstValueOfAPartIsTakenAtOnce()
    {
        PartConfirmStep step = PartConfirm.Step(PartConfirmState.Empty, Msg(60, 0));

        Assert.AreEqual(ConfirmOutcome.First, step.Outcome);
        Assert.AreEqual(60, step.State.Held.Percent);
        Assert.AreEqual(Epoch, step.State.Held.ReadAt);
    }

    [TestMethod]
    public void AMessageWithNoValueForThePartChangesNothing()
    {
        PartConfirmState held = Feed(PartConfirmState.Empty, Msg(60, 0));

        PartConfirmStep step = PartConfirm.Step(held, PartReading.Unknown);

        Assert.AreEqual(ConfirmOutcome.NoValue, step.Outcome);
        Assert.AreEqual(held, step.State);
    }

    [TestMethod]
    public void OneStrayValueAmongSixtiesChangesNothing()
    {
        PartConfirmState state = Feed(PartConfirmState.Empty, Msg(60, 0), Msg(60, 2), Msg(40, 4), Msg(60, 6), Msg(60, 8));

        Assert.AreEqual(60, state.Held.Percent, "The stray 40 was never held.");
        Assert.AreEqual(Epoch + TimeSpan.FromSeconds(8), state.Held.ReadAt);
    }

    [TestMethod]
    public void TwoSendersAlternatingHoldTheLastConfirmedValue()
    {
        PartConfirmState state = Feed(PartConfirmState.Empty, Msg(60, 0), Msg(60, 2));
        for (int i = 0; i < 6; i++)
        {
            state = Feed(state, Msg(70, 4 + i * 2), Msg(60, 5 + i * 2));
        }

        Assert.AreEqual(60, state.Held.Percent);
    }

    [TestMethod]
    public void TwoSendersAlternatingAreReportedAsAlternating()
    {
        PartConfirmState state = Feed(PartConfirmState.Empty, Msg(60, 0));
        var outcomes = new List<ConfirmOutcome>();
        for (int i = 0; i < 4; i++)
        {
            foreach (PartReading m in new[] { Msg(70, 2 + i * 2), Msg(60, 3 + i * 2) })
            {
                PartConfirmStep step = PartConfirm.Step(state, m);
                state = step.State;
                outcomes.Add(step.Outcome);
            }
        }

        Assert.Contains(ConfirmOutcome.Alternating, outcomes);
        Assert.AreEqual(1, outcomes.Count(o => o == ConfirmOutcome.Discarded), "The first throw-out alone is a stray, not an alternation.");
    }

    [TestMethod]
    public void TwoMessagesInARowSayingTheSameNewValueMoveIt()
    {
        PartConfirmStep first = PartConfirm.Step(Feed(PartConfirmState.Empty, Msg(60, 0)), Msg(70, 2));
        Assert.AreEqual(ConfirmOutcome.Candidate, first.Outcome);
        Assert.AreEqual(60, first.State.Held.Percent, "One message is not enough.");

        PartConfirmStep second = PartConfirm.Step(first.State, Msg(70, 3));

        Assert.AreEqual(ConfirmOutcome.Confirmed, second.Outcome);
        Assert.AreEqual(70, second.State.Held.Percent);
        Assert.AreEqual(Epoch + TimeSpan.FromSeconds(3), second.State.Held.ReadAt);
    }

    [TestMethod]
    public void TwoMessagesFurtherApartThanTheWindowDoNotConfirm()
    {
        PartConfirmState state = Feed(PartConfirmState.Empty, Msg(60, 0), Msg(70, 2));

        PartConfirmStep late = PartConfirm.Step(state, Msg(70, 2 + BroadcastRules.Window.TotalSeconds + 1));

        Assert.AreEqual(60, late.State.Held.Percent, "The first 70 had timed out.");
        Assert.AreEqual(ConfirmOutcome.Candidate, late.Outcome);

        PartConfirmStep next = PartConfirm.Step(late.State, Msg(70, 2 + BroadcastRules.Window.TotalSeconds + 2));
        Assert.AreEqual(70, next.State.Held.Percent, "The late 70 starts the count again, and the next one confirms it.");
    }

    [TestMethod]
    public void TwoMessagesExactlyAWindowApartConfirm()
    {
        PartConfirmState state = Feed(PartConfirmState.Empty, Msg(60, 0), Msg(70, 2), Msg(70, 2 + BroadcastRules.Window.TotalSeconds));

        Assert.AreEqual(70, state.Held.Percent);
    }

    [TestMethod]
    public void TheChargingFlagIsConfirmedTheSameWay()
    {
        PartConfirmState state = Feed(PartConfirmState.Empty, Msg(60, 0, charging: false), Msg(60, 2, charging: true));
        Assert.AreEqual(false, state.Held.Charging, "One message with a new flag changes nothing.");

        state = Feed(state, Msg(60, 3, charging: false), Msg(60, 4, charging: true));
        Assert.AreEqual(false, state.Held.Charging, "A flag that flips back and forth never settles.");

        state = Feed(state, Msg(60, 5, charging: true));
        Assert.AreEqual(true, state.Held.Charging, "Two in a row do.");
        Assert.AreEqual(60, state.Held.Percent);
    }

    [TestMethod]
    public void AValueAndAFlagMustBothRepeat()
    {
        PartConfirmState state = Feed(PartConfirmState.Empty, Msg(60, 0, charging: false), Msg(70, 2, charging: true), Msg(70, 3, charging: false));

        Assert.AreEqual(60, state.Held.Percent, "70 charging and 70 not charging are two different things.");
    }

    [TestMethod]
    public void TheReadTimeMovesOnWithEveryMessageThatRepeatsTheValue()
    {
        PartConfirmState state = Feed(PartConfirmState.Empty, Msg(60, 0));
        for (int s = 2; s <= 20; s += 2)
        {
            state = Feed(state, Msg(60, s));
            Assert.AreEqual(Epoch + TimeSpan.FromSeconds(s), state.Held.ReadAt);
        }
    }

    [TestMethod]
    public void TheEarStateFollowsEveryMessageWhateverIsHeldOfTheValue()
    {
        PartConfirmState state = Feed(PartConfirmState.Empty, Msg(60, 0, inEar: true));

        state = Feed(state, Msg(30, 2, inEar: false));

        Assert.AreEqual(60, state.Held.Percent);
        Assert.AreEqual(false, state.Held.InEar, "Ear detection is not one of the confirmed readings.");
    }

    [TestMethod]
    public void ARepeatOfTheHeldValueDropsAWaitingValue()
    {
        PartConfirmState state = Feed(PartConfirmState.Empty, Msg(60, 0), Msg(70, 2), Msg(60, 3));

        Assert.IsNull(state.Pending);

        state = Feed(state, Msg(70, 4));
        Assert.AreEqual(60, state.Held.Percent, "The 70 at 4 s is the first again, not the second.");
    }
}
