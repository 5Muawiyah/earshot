using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The send-to picker as data: its rows for each place the AirPods can be, and what a press on each row reaches. Nothing here
// opens a window, connects or hands anything back.
[TestClass]
public sealed class SendToModelTests
{
    private sealed class Calls
    {
        public int Connect { get; set; }

        public int HandBack { get; set; }

        public int Speakers { get; set; }

        public SendToActions Actions => new(() => Connect++, () => HandBack++, () => Speakers++);
    }

    [TestMethod]
    public void OnThisPcTheFirstRowIsCurrentAndTheSecondSendsThemToTheOtherDevice()
    {
        SendToModel model = SendToModel.From(AirPodsWhere.ThisPc, "iPhone", busy: false);

        Assert.AreEqual("On this PC", model.Where);
        Assert.HasCount(3, model.Rows);
        Assert.AreEqual(SendToChoice.ThisPc, model.Rows[0].Choice);
        Assert.IsTrue(model.Rows[0].Current);
        Assert.IsFalse(model.Rows[0].Acts, "They are here already, so pressing it does nothing.");
        Assert.AreEqual("Send to iPhone", model.Rows[1].Label);
        Assert.IsFalse(model.Rows[1].Current);
        Assert.IsTrue(model.Rows[1].Acts);
        Assert.AreEqual("Other speakers", model.Rows[2].Label);
        Assert.IsTrue(model.Rows[2].Acts);
    }

    [TestMethod]
    public void OnTheOtherDeviceItsNameIsTheCurrentPlaceAndTheFirstRowBringsThemHere()
    {
        SendToModel model = SendToModel.From(AirPodsWhere.Elsewhere, "iPhone", busy: false);

        Assert.AreEqual("On your iPhone", model.Where);
        Assert.IsTrue(model.Rows[0].Acts);
        Assert.IsTrue(model.Rows[1].Current);
        Assert.IsFalse(model.Rows[1].Acts);
    }

    [TestMethod]
    [DataRow(AirPodsWhere.NotInUse)]
    [DataRow(AirPodsWhere.Unknown)]
    public void WhenTheyAreInUseNowhereTheOtherDeviceIsNotNamedAsAPlaceAndHasNothingToSend(AirPodsWhere where)
    {
        SendToModel model = SendToModel.From(where, "iPhone", busy: false);

        Assert.AreEqual("Not on this PC", model.Where);
        Assert.DoesNotContain("iPhone", model.Where, "The device's name is a place only where the AirPods are on it.");
        Assert.IsTrue(model.Rows[0].Acts, "Bring them here.");
        Assert.IsFalse(model.Rows[1].Enabled, "There is nothing on this PC to send.");
        Assert.IsTrue(model.Rows[2].Acts);
    }

    [TestMethod]
    public void AClearedNameReadsAnotherDevice()
    {
        Assert.AreEqual("Send to another device", SendToModel.From(AirPodsWhere.ThisPc, "", busy: false).Rows[1].Label);
        Assert.AreEqual("Send to another device", SendToModel.From(AirPodsWhere.ThisPc, "  ", busy: false).Rows[1].Label);
        Assert.AreEqual("Send to My phone", SendToModel.From(AirPodsWhere.ThisPc, " My phone ", busy: false).Rows[1].Label);
    }

    [TestMethod]
    public void WhileASwitchIsInFlightTheTwoSwitchingRowsAreHeldAndSoundSettingsIsNot()
    {
        SendToModel model = SendToModel.From(AirPodsWhere.ThisPc, "iPhone", busy: true);

        Assert.IsFalse(model.Rows[0].Enabled);
        Assert.IsFalse(model.Rows[1].Enabled);
        Assert.IsTrue(model.Rows[2].Enabled);
    }

    [TestMethod]
    public void ThisPcConnectsAndTheOtherDeviceHandsBackAndNothingElseMoves()
    {
        var calls = new Calls();
        SendToActions actions = calls.Actions;

        Assert.IsTrue(actions.Choose(SendToModel.From(AirPodsWhere.Elsewhere, "iPhone", false), SendToChoice.ThisPc));
        Assert.IsTrue(actions.Choose(SendToModel.From(AirPodsWhere.ThisPc, "iPhone", false), SendToChoice.OtherDevice));

        Assert.AreEqual(1, calls.Connect);
        Assert.AreEqual(1, calls.HandBack);
        Assert.AreEqual(0, calls.Speakers);
    }

    [TestMethod]
    [DataRow(AirPodsWhere.ThisPc)]
    [DataRow(AirPodsWhere.Elsewhere)]
    [DataRow(AirPodsWhere.NotInUse)]
    [DataRow(AirPodsWhere.Unknown)]
    public void OtherSpeakersOnlyOpensSoundSettingsAndNeverHandsBackOrConnects(AirPodsWhere where)
    {
        var calls = new Calls();

        bool ran = calls.Actions.Choose(SendToModel.From(where, "iPhone", busy: false), SendToChoice.OtherSpeakers);

        Assert.IsTrue(ran);
        Assert.AreEqual(1, calls.Speakers);
        Assert.AreEqual(0, calls.HandBack, "Other speakers must never hand the AirPods back.");
        Assert.AreEqual(0, calls.Connect);
    }

    [TestMethod]
    public void OtherSpeakersStillWorksWhileASwitchIsInFlight()
    {
        var calls = new Calls();

        calls.Actions.Choose(SendToModel.From(AirPodsWhere.ThisPc, "iPhone", busy: true), SendToChoice.OtherSpeakers);

        Assert.AreEqual(1, calls.Speakers);
        Assert.AreEqual(0, calls.HandBack);
    }

    [TestMethod]
    public void ARowThatIsHeldOrAlreadyCurrentDoesNothing()
    {
        var calls = new Calls();
        SendToActions actions = calls.Actions;

        Assert.IsFalse(actions.Choose(SendToModel.From(AirPodsWhere.ThisPc, "iPhone", false), SendToChoice.ThisPc), "Already here.");
        Assert.IsFalse(actions.Choose(SendToModel.From(AirPodsWhere.Elsewhere, "iPhone", false), SendToChoice.OtherDevice), "Already there.");
        Assert.IsFalse(actions.Choose(SendToModel.From(AirPodsWhere.NotInUse, "iPhone", false), SendToChoice.OtherDevice), "Nothing to send.");
        Assert.IsFalse(actions.Choose(SendToModel.From(AirPodsWhere.ThisPc, "iPhone", true), SendToChoice.OtherDevice), "A switch is in flight.");

        Assert.AreEqual(0, calls.Connect + calls.HandBack + calls.Speakers);
    }

    [TestMethod]
    public void ARowNamesItselfForAScreenReaderWithItsPlace()
    {
        Assert.AreEqual("This PC, current", SendToCopy.RowName("This PC", current: true));
        Assert.AreEqual("Other speakers", SendToCopy.RowName("Other speakers", current: false));
    }
}
