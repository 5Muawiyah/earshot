using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// OneCardArbiter: one test for each row of the one card spec's table (5.2), named by the row, then the rules the rows rest on.
[TestClass]
public sealed class OneCardArbiterTests
{
    private static readonly TimeSpan Six = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan Twelve = TimeSpan.FromSeconds(12);

    private static CardState State(CardShowing showing, UnaskedKind? kind = null) => new(showing, kind);

    private static CardDecision Decide(CardState state, CardEvent ev) => OneCardArbiter.Decide(state, ev);

    // Row 1: an asked card, any page, and the case opens.
    [TestMethod]
    [DataRow("AskedMain")]
    [DataRow("AskedSubPage")]
    public void Row01AnAskedCardTakesTheCaseOpeningAsNothingNewAndLogsTheRefusal(string showingName)
    {
        CardShowing showing = Enum.Parse<CardShowing>(showingName);
        CardDecision decision = Decide(State(showing), CardEvent.Opens(UnaskedKind.CaseOpened));

        Assert.AreEqual(CardDoes.UpdateValuesOnly, decision.Does);
        Assert.IsTrue(decision.LogRefusal, "The refusal is logged.");
    }

    // Row 2: an asked card, and moved to, low or charged: nothing opens and nothing is queued.
    [TestMethod]
    [DataRow("AskedMain", "MovedTo")]
    [DataRow("AskedMain", "LowFallback")]
    [DataRow("AskedMain", "ChargedFallback")]
    [DataRow("AskedSubPage", "MovedTo")]
    [DataRow("AskedSubPage", "LowFallback")]
    [DataRow("AskedSubPage", "ChargedFallback")]
    public void Row02AnAskedCardOpensNothingForMovedToLowOrChargedAndQueuesNothing(string showingName, string kindName)
    {
        CardShowing showing = Enum.Parse<CardShowing>(showingName);
        UnaskedKind kind = Enum.Parse<UnaskedKind>(kindName);
        CardDecision decision = Decide(State(showing), CardEvent.Opens(kind));

        Assert.AreEqual(CardDoes.Nothing, decision.Does);
        Assert.IsFalse(decision.LogRefusal);
        Assert.IsNull(decision.Hold);
    }

    // Row 3: the asked card's main page and a notice: the status row carries the line for the hold, 6 s, 12 s with a button.
    [TestMethod]
    public void Row03AMainPageCarriesANoticeInItsStatusRowForSixSeconds()
    {
        CardDecision decision = Decide(State(CardShowing.AskedMain), CardEvent.Opens(UnaskedKind.Notice));

        Assert.AreEqual(CardDoes.StatusRowCarries, decision.Does);
        Assert.AreEqual(Six, decision.Hold);
    }

    [TestMethod]
    public void Row03AndTwelveSecondsWithAButton()
    {
        CardDecision decision = Decide(State(CardShowing.AskedMain), CardEvent.Opens(UnaskedKind.Notice, hasAction: true));

        Assert.AreEqual(CardDoes.StatusRowCarries, decision.Does);
        Assert.AreEqual(Twelve, decision.Hold);
    }

    // Row 4: a settings or update page and a notice: a one-row strip at the foot of the page for the hold.
    [TestMethod]
    public void Row04ASubPageCarriesANoticeInAStripAtItsFootForTheHold()
    {
        Assert.AreEqual(new CardDecision(CardDoes.FooterStrip, Six), Decide(State(CardShowing.AskedSubPage), CardEvent.Opens(UnaskedKind.Notice)));
        Assert.AreEqual(new CardDecision(CardDoes.FooterStrip, Twelve), Decide(State(CardShowing.AskedSubPage), CardEvent.Opens(UnaskedKind.Notice, hasAction: true)));
    }

    // Row 5: any unasked card, and the owner asks for the card: the unasked one closes and the asked one opens.
    [TestMethod]
    [DataRow("UnaskedFull")]
    [DataRow("CompactHolding")]
    [DataRow("CompactFolding")]
    public void Row05AnAskedOpeningClosesAnyUnaskedCardAndOpensTheAskedOne(string showingName)
    {
        CardShowing showing = Enum.Parse<CardShowing>(showingName);
        CardDecision decision = Decide(State(showing, UnaskedKind.MovedTo), new CardEvent(CardEventKind.AskedOpen));

        Assert.AreEqual(CardDoes.CloseUnaskedOpenAsked, decision.Does);
    }

    // Row 6: the compact card and the case opens: it morphs in place into the full case opened card.
    [TestMethod]
    [DataRow("CompactHolding")]
    [DataRow("CompactFolding")]
    public void Row06TheCaseOpeningMorphsACompactCardInPlaceIntoTheFullOne(string showingName)
    {
        CardShowing showing = Enum.Parse<CardShowing>(showingName);
        CardDecision decision = Decide(State(showing, UnaskedKind.MovedTo), CardEvent.Opens(UnaskedKind.CaseOpened));

        Assert.AreEqual(CardDoes.MorphToFull, decision.Does);
    }

    // Row 7: a click on the compact card's body grows it to the full card, with no timer.
    [TestMethod]
    public void Row07ABodyClickGrowsTheCompactCardToTheFullOneWithNoTimer()
    {
        CardDecision decision = Decide(State(CardShowing.CompactHolding, UnaskedKind.MovedTo), new CardEvent(CardEventKind.BodyClick));

        Assert.AreEqual(CardDoes.GrowToFull, decision.Does);
        Assert.IsNull(decision.Hold, "No timer: it closes by its close button.");
    }

    // Row 8: the pointer over a holding compact card pauses the hold; leaving resumes it with the time that remained.
    [TestMethod]
    public void Row08ThePointerOverAHoldingCompactCardPausesItAndLeavingResumesIt()
    {
        CardState holding = State(CardShowing.CompactHolding, UnaskedKind.MovedTo);

        Assert.AreEqual(CardDoes.PauseHold, Decide(holding, new CardEvent(CardEventKind.PointerOver)).Does);
        Assert.AreEqual(CardDoes.ResumeHold, Decide(holding, new CardEvent(CardEventKind.PointerLeave)).Does);
    }

    // Row 9: a Connect from the holding compact card's own button is a take back.
    [TestMethod]
    public void Row09AConnectFromTheCompactCardsOwnButtonIsATakeBack()
    {
        Assert.AreEqual(CardDoes.TakeBack, Decide(State(CardShowing.CompactHolding, UnaskedKind.MovedTo), new CardEvent(CardEventKind.ConnectOwnButton)).Does);
    }

    // Row 10: moved to holding, and a Connect from elsewhere: it is no longer true, so the card folds now.
    [TestMethod]
    public void Row10AConnectFromElsewhereFoldsAMovedToCardAtOnce()
    {
        Assert.AreEqual(CardDoes.FoldNow, Decide(State(CardShowing.CompactHolding, UnaskedKind.MovedTo), new CardEvent(CardEventKind.ConnectElsewhere)).Does);
    }

    // Row 11: a low, charged or notice card holding, and a Connect from elsewhere: it stays to its hold.
    [TestMethod]
    [DataRow("LowFallback")]
    [DataRow("ChargedFallback")]
    [DataRow("Notice")]
    public void Row11AConnectFromElsewhereLeavesALowChargedOrNoticeCardToItsHold(string kindName)
    {
        UnaskedKind kind = Enum.Parse<UnaskedKind>(kindName);
        Assert.AreEqual(CardDoes.StayToHold, Decide(State(CardShowing.CompactHolding, kind), new CardEvent(CardEventKind.ConnectElsewhere)).Does);
    }

    // Row 12: a compact card folding away, and a Connect from its own button: the fold is cancelled and the button shows progress.
    [TestMethod]
    public void Row12AConnectFromTheOwnButtonOfAFoldingCardCancelsTheFold()
    {
        Assert.AreEqual(CardDoes.CancelFoldShowProgress, Decide(State(CardShowing.CompactFolding, UnaskedKind.MovedTo), new CardEvent(CardEventKind.ConnectOwnButton)).Does);
    }

    // Row 13: a compact card folding away, and a Connect from elsewhere: the fold finishes and the gauge tells the story.
    [TestMethod]
    public void Row13AConnectFromElsewhereLetsAFoldingCardFinishFolding()
    {
        Assert.AreEqual(CardDoes.FoldFinishes, Decide(State(CardShowing.CompactFolding, UnaskedKind.MovedTo), new CardEvent(CardEventKind.ConnectElsewhere)).Does);
    }

    // Row 14: a compact card of lower or equal priority, and a new compact of equal or higher: the content is replaced in place and
    // the hold restarts.
    [TestMethod]
    [DataRow("ChargedFallback", "LowFallback")]
    [DataRow("ChargedFallback", "ChargedFallback")]
    [DataRow("Notice", "MovedTo")]
    [DataRow("LowFallback", "MovedTo")]
    [DataRow("MovedTo", "MovedTo")]
    public void Row14ANewCompactOfEqualOrHigherPriorityReplacesTheContentInPlaceAndRestartsTheHold(string currentName, string incomingName)
    {
        UnaskedKind current = Enum.Parse<UnaskedKind>(currentName);
        UnaskedKind incoming = Enum.Parse<UnaskedKind>(incomingName);
        CardDecision decision = Decide(State(CardShowing.CompactHolding, current), CardEvent.Opens(incoming));

        Assert.AreEqual(CardDoes.ReplaceInPlace, decision.Does);
        Assert.AreEqual(Six, decision.Hold, "The hold restarts.");
    }

    // Row 15: a compact or full unasked card of higher priority, and a new one that is lower: it waits in its slot.
    [TestMethod]
    [DataRow("CompactHolding", "MovedTo", "LowFallback")]
    [DataRow("CompactHolding", "LowFallback", "ChargedFallback")]
    [DataRow("CompactHolding", "MovedTo", "Notice")]
    [DataRow("UnaskedFull", "CaseOpened", "MovedTo")]
    [DataRow("UnaskedFull", "CaseOpened", "LowFallback")]
    [DataRow("UnaskedFull", "ConnectFailed", "ChargedFallback")]
    public void Row15ALowerPriorityCardWaitsItsTurnInItsSlot(string showingName, string currentName, string incomingName)
    {
        CardShowing showing = Enum.Parse<CardShowing>(showingName);
        UnaskedKind current = Enum.Parse<UnaskedKind>(currentName);
        UnaskedKind incoming = Enum.Parse<UnaskedKind>(incomingName);
        CardDecision decision = Decide(State(showing, current), CardEvent.Opens(incoming));

        Assert.AreEqual(CardDoes.WaitInSlot, decision.Does);
        Assert.IsNull(decision.Hold, "Nothing starts until its turn.");
    }

    // Row 16: nothing showing, and anything: it opens, subject to the gates.
    [TestMethod]
    [DataRow("CaseOpened")]
    [DataRow("ConnectFailed")]
    [DataRow("MovedTo")]
    [DataRow("LowFallback")]
    [DataRow("ChargedFallback")]
    [DataRow("Notice")]
    public void Row16WithNothingShowingAnythingOpens(string kindName)
    {
        UnaskedKind kind = Enum.Parse<UnaskedKind>(kindName);
        Assert.AreEqual(CardDoes.Open, Decide(State(CardShowing.Nothing), CardEvent.Opens(kind)).Does);
    }

    [TestMethod]
    public void Row16AnAskedOpeningOpensToo()
    {
        Assert.AreEqual(CardDoes.Open, Decide(State(CardShowing.Nothing), new CardEvent(CardEventKind.AskedOpen)).Does);
    }

    // ---- The rules the rows rest on

    [TestMethod]
    public void ALowerNumberOutranksAHigherOneAndTheOrderIsTheDesigns()
    {
        UnaskedKind[] order =
            [UnaskedKind.CaseOpened, UnaskedKind.ConnectFailed, UnaskedKind.MovedTo, UnaskedKind.LowFallback, UnaskedKind.ChargedFallback, UnaskedKind.Notice];

        for (int i = 0; i < order.Length; i++)
        {
            for (int j = 0; j < order.Length; j++)
            {
                Assert.AreEqual(i <= j, OneCardArbiter.OutranksOrEquals(order[i], order[j]), order[i] + " against " + order[j]);
            }
        }
    }

    [TestMethod]
    public void TheHoldIsSixSecondsTwelveWithAButtonAndNoneForANoticeThatStaysUp()
    {
        Assert.AreEqual(Six, OneCardArbiter.NoticeHold(hasAction: false, staysUp: false));
        Assert.AreEqual(Twelve, OneCardArbiter.NoticeHold(hasAction: true, staysUp: false));
        Assert.IsNull(OneCardArbiter.NoticeHold(hasAction: true, staysUp: true));
        Assert.IsNull(OneCardArbiter.NoticeHold(hasAction: false, staysUp: true));
        Assert.AreEqual(Six, OneCardArbiter.CompactHold);
    }

    [TestMethod]
    public void ACardThatIsOpeningHoldsSixSecondsExceptTheFullOnes()
    {
        Assert.AreEqual(Six, Decide(State(CardShowing.Nothing), CardEvent.Opens(UnaskedKind.MovedTo)).Hold);
        Assert.AreEqual(Six, Decide(State(CardShowing.Nothing), CardEvent.Opens(UnaskedKind.LowFallback)).Hold);
        Assert.AreEqual(Six, Decide(State(CardShowing.Nothing), CardEvent.Opens(UnaskedKind.ChargedFallback)).Hold);
        Assert.AreEqual(Twelve, Decide(State(CardShowing.Nothing), CardEvent.Opens(UnaskedKind.Notice, hasAction: true)).Hold);
        Assert.IsNull(Decide(State(CardShowing.Nothing), CardEvent.Opens(UnaskedKind.Notice, staysUp: true)).Hold);
        Assert.IsNull(Decide(State(CardShowing.Nothing), CardEvent.Opens(UnaskedKind.CaseOpened)).Hold, "The case opened card closes with the case, not on a timer here.");
    }

    // A card on its way out is dismissed and stays dismissed: only a Connect from its own button, or the case opening, brings it back.
    [TestMethod]
    public void AFoldingCardIgnoresTheBodyAndThePointer()
    {
        CardState folding = State(CardShowing.CompactFolding, UnaskedKind.MovedTo);

        Assert.AreEqual(CardDoes.Nothing, Decide(folding, new CardEvent(CardEventKind.BodyClick)).Does);
        Assert.AreEqual(CardDoes.Nothing, Decide(folding, new CardEvent(CardEventKind.PointerOver)).Does);
        Assert.AreEqual(CardDoes.Nothing, Decide(folding, new CardEvent(CardEventKind.PointerLeave)).Does);
    }

    // The asked card ignores what is not a notice or the case opening; a click or pointer is the card's own business there.
    [TestMethod]
    public void AnAskedCardIgnoresTheEventsOfTheCompactCard()
    {
        foreach (CardShowing showing in new[] { CardShowing.AskedMain, CardShowing.AskedSubPage })
        {
            foreach (CardEventKind kind in new[] { CardEventKind.BodyClick, CardEventKind.PointerOver, CardEventKind.PointerLeave, CardEventKind.ConnectOwnButton, CardEventKind.ConnectElsewhere })
            {
                Assert.AreEqual(CardDoes.Nothing, Decide(State(showing), new CardEvent(kind)).Does, showing + " " + kind);
            }
        }
    }

    [TestMethod]
    public void AFullUnaskedCardTakesAnEqualOrHigherCardInPlace()
    {
        Assert.AreEqual(CardDoes.ReplaceInPlace, Decide(State(CardShowing.UnaskedFull, UnaskedKind.ConnectFailed), CardEvent.Opens(UnaskedKind.CaseOpened)).Does);
        Assert.AreEqual(CardDoes.ReplaceInPlace, Decide(State(CardShowing.UnaskedFull, UnaskedKind.CaseOpened), CardEvent.Opens(UnaskedKind.CaseOpened)).Does);
    }

    // The arbiter decides; it never connects. Its result names a take back, and what a take back does is the card's own genuine click.
    [TestMethod]
    public void NoEventButAGenuineClickOnTheOwnButtonAsksForATakeBack()
    {
        foreach (CardShowing showing in Enum.GetValues<CardShowing>())
        {
            foreach (UnaskedKind kind in Enum.GetValues<UnaskedKind>())
            {
                foreach (CardEventKind ev in Enum.GetValues<CardEventKind>())
                {
                    CardDecision decision = Decide(State(showing, kind), new CardEvent(ev, kind));
                    if (decision.Does == CardDoes.TakeBack)
                    {
                        Assert.AreEqual(CardEventKind.ConnectOwnButton, ev, "Only the compact card's own button takes back: " + showing + " " + kind);
                        Assert.AreEqual(CardShowing.CompactHolding, showing);
                    }
                }
            }
        }
    }
}
