using Earshot.App;
using Earshot.Audio.Connect;
using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.App;

// Whose side a failed connect is on, from the codes Windows returned. The table is the connect's contract: a temporary code is
// tried once more, this PC's side can lead to Fix connection, and the AirPods' side never does.
[TestClass]
public sealed class ConnectFailureClassTests
{
    private static ConnectResult Sent(ConnectOutcome outcome, params int[] codes) =>
        new(outcome, ConnectMessages.CouldNotReachDriver,
            codes.Select((c, i) => StepOutcomes.FromHResult(KsConnectPath.ReconnectStep + ":filter" + i, c, "a2dp: adapter", ok: c == 0)).ToList());

    [TestMethod]
    [DataRow(unchecked((int)0x88890004))]
    [DataRow(unchecked((int)0x80070490))]
    [DataRow(unchecked((int)0x8007048F))]
    [DataRow(unchecked((int)0x80070015))]
    [DataRow(unchecked((int)0x800700AA))]
    [DataRow(unchecked((int)0x80070079))]
    [DataRow(unchecked((int)0x800705B4))]
    [DataRow(unchecked((int)0x8007001F))]
    public void TheTemporaryCodesAreRetriedOnceThenThisPcsSide(int code)
    {
        ConnectResult result = Sent(ConnectOutcome.NoFiltersResponded, code);

        Assert.AreEqual(FailureClass.Temporary, ConnectFailureClassifier.Classify(result, DeviceHint.Unknown, retried: false).Class);
        Assert.AreEqual(FailureClass.PcSide, ConnectFailureClassifier.Classify(result, DeviceHint.Unknown, retried: true).Class);
    }

    [TestMethod]
    public void AnAccessRefusalIsThisPcsSideAndNeverRetried()
    {
        ConnectFailure refused = ConnectFailureClassifier.Classify(Sent(ConnectOutcome.NoFiltersResponded, unchecked((int)0x80070005)), DeviceHint.HeardNearby, retried: false);

        Assert.AreEqual(FailureClass.PcSide, refused.Class);
        Assert.AreEqual("Windows refused the change.", refused.Reason);
        Assert.AreEqual("0x80070005", refused.Code);
    }

    [TestMethod]
    public void NotSupportedFromEveryFilterIsTheDriversAnswer()
    {
        ConnectFailure all = ConnectFailureClassifier.Classify(
            Sent(ConnectOutcome.NoFiltersResponded, unchecked((int)0x80070032), unchecked((int)0x80004001), unchecked((int)0x80070492)), DeviceHint.Unknown, retried: false);
        ConnectFailure oneOnly = ConnectFailureClassifier.Classify(
            Sent(ConnectOutcome.NoFiltersResponded, unchecked((int)0x80070032), unchecked((int)0x80004005)), DeviceHint.Unknown, retried: false);

        Assert.AreEqual(FailureClass.PcSide, all.Class);
        Assert.AreEqual("The audio driver did not accept the request.", all.Reason);
        Assert.AreEqual("Could not reach the AirPods audio driver. Try again.", oneOnly.Reason, "One filter saying something else is not the driver refusing everything.");
    }

    [TestMethod]
    public void AnUnknownCodeIsThisPcsSideAndKeepsItsRawValue()
    {
        ConnectFailure failure = ConnectFailureClassifier.Classify(Sent(ConnectOutcome.NoFiltersResponded, unchecked((int)0x80004005)), DeviceHint.Unknown, retried: false);

        Assert.AreEqual(FailureClass.PcSide, failure.Class);
        Assert.AreEqual("0x80004005", failure.Code);
    }

    [TestMethod]
    public void NothingSentIsNotATemporaryCode()
    {
        var result = new ConnectResult(ConnectOutcome.NoFiltersResponded, ConnectMessages.CouldNotReachDriver,
            [StepOutcomes.NotAttempted(KsConnectPath.ReconnectStep + ":src", "a2dp: adapter")]);

        Assert.AreEqual(FailureClass.PcSide, ConnectFailureClassifier.Classify(result, DeviceHint.Unknown, retried: false).Class);
    }

    [TestMethod]
    [DataRow("LidClosed", "Open the case near this PC.")]
    [DataRow("InUseElsewhere", "In use on your iPhone.")]
    [DataRow("NotHeard", "Not heard nearby.")]
    public void ARequestThatWasAcceptedAndNeverConnectedIsTheDevicesSideUnlessTheyAreHeardNearby(string hintName, string reason)
    {
        var hint = Enum.Parse<DeviceHint>(hintName);
        ConnectFailure failure = ConnectFailureClassifier.Classify(
            new ConnectResult(ConnectOutcome.AttemptedTimedOut, ConnectMessages.StillConnecting, []), hint, retried: false);

        Assert.AreEqual(FailureClass.DeviceSide, failure.Class);
        Assert.AreEqual(reason, failure.Reason);
    }

    [TestMethod]
    public void ARequestThatWasAcceptedWithNothingHeardFromTheAirIsItsOwnClassAndAsksForTheCase()
    {
        ConnectFailure failure = ConnectFailureClassifier.Classify(
            new ConnectResult(ConnectOutcome.AttemptedTimedOut, ConnectMessages.StillConnecting, []), DeviceHint.Unknown, retried: false);

        Assert.AreEqual(FailureClass.Unknown, failure.Class);
        Assert.AreEqual("Open the case near this PC and try again.", failure.Reason);
    }

    [TestMethod]
    public void AcceptedAndHeardNearbyIsTemporaryOnceThenPairingAgain()
    {
        var timedOut = new ConnectResult(ConnectOutcome.AttemptedTimedOut, ConnectMessages.StillConnecting, []);

        ConnectFailure first = ConnectFailureClassifier.Classify(timedOut, DeviceHint.HeardNearby, retried: false);
        ConnectFailure second = ConnectFailureClassifier.Classify(timedOut, DeviceHint.HeardNearby, retried: true);

        Assert.AreEqual(FailureClass.Temporary, first.Class);
        Assert.AreEqual(FailureClass.PcSide, second.Class);
        Assert.AreEqual("The AirPods did not answer this PC. They may need pairing again.", second.Reason);
    }

    [TestMethod]
    public void AnEndpointThatDoesNotComeBackIsTemporaryOnceThenWindowsDidNotBringThemBack()
    {
        Assert.AreEqual(FailureClass.Temporary, ConnectFailureClassifier.EndpointNotBack(retried: false).Class);
        ConnectFailure second = ConnectFailureClassifier.EndpointNotBack(retried: true);
        Assert.AreEqual(FailureClass.PcSide, second.Class);
        Assert.AreEqual("Windows did not bring the AirPods back.", second.Reason);
    }

    [TestMethod]
    public void NoPairedDeviceIsThisPcsSideWithTheNeedPairingSentence()
    {
        ConnectFailure failure = ConnectFailureClassifier.Classify(
            new ConnectResult(ConnectOutcome.NotFound, ConnectMessages.NotFound, []), DeviceHint.Unknown, retried: false);

        Assert.AreEqual(FailureClass.PcSide, failure.Class);
        Assert.AreEqual("These AirPods need pairing again.", failure.Reason);
    }

    [TestMethod]
    public void OutputTurnedOffInSoundSettingsIsThisPcsSideWithItsOwnMessage()
    {
        ConnectFailure failure = ConnectFailureClassifier.Classify(
            new ConnectResult(ConnectOutcome.Failed, ConnectMessages.OutputTurnedOff, []), DeviceHint.Unknown, retried: false);

        Assert.AreEqual(FailureClass.PcSide, failure.Class);
        Assert.AreEqual(ConnectMessages.OutputTurnedOff, failure.Reason);
    }

    [TestMethod]
    public void AnAllowThatLeftPartOfTheNodesOffIsThisPcsSide()
    {
        var partial = new ControllerResult(OpStatus.Partial, "x", []);

        ConnectFailure failure = ConnectFailureClassifier.AllowFailed(partial);

        Assert.AreEqual(FailureClass.PcSide, failure.Class);
        Assert.AreEqual("Part of the AirPods stayed off.", failure.Reason);
    }

    [TestMethod]
    public void AGateThatRefusedForItsSetUpKeepsItsOwnMessage()
    {
        ControllerResult refused = ControllerResult.Fail("Boot block is not set up yet. Choose Set up Earshot or Repair Earshot.",
            [new StepOutcome("gate-exit", false, 6, "no-identity", null)]);

        ConnectFailure failure = ConnectFailureClassifier.AllowFailed(refused);

        Assert.AreEqual(FailureClass.PcSide, failure.Class);
        Assert.AreEqual(refused.UserMessage, failure.Reason);
        Assert.AreEqual("no-identity", failure.Code);
    }

    [TestMethod]
    public void AConfirmedConnectIsNoFailure()
    {
        Assert.AreEqual(FailureClass.None, ConnectFailureClassifier.Classify(
            new ConnectResult(ConnectOutcome.Confirmed, ConnectMessages.Connected, []), DeviceHint.Unknown, retried: false).Class);
    }
}
