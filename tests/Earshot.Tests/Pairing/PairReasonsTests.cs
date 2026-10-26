using System.Text.RegularExpressions;
using Earshot.Pairing;
using Earshot.Repair;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Pairing;

// The reason table of the pair design: every DevicePairingResultStatus value has one card line that claims only Microsoft's
// documented meaning.
// https://learn.microsoft.com/en-us/uwp/api/windows.devices.enumeration.devicepairingresultstatus
[TestClass]
public sealed class PairReasonsTests
{
    private static PairingCallResult Status(int code) => new("status", code, code is 0 or 3, null);

    [TestMethod]
    public void EveryDocumentedStatusHasItsOwnLineFromTheTable()
    {
        var expected = new Dictionary<int, string>
        {
            [0] = "Paired.",
            [1] = "Windows says the AirPods aren't ready to pair. Put the case in pairing mode and try again.",
            [2] = "Windows didn't pair the AirPods.",
            [3] = "Paired.",
            [4] = "The device turned down the connection.",
            [5] = "The device can't take another connection right now.",
            [6] = "Windows reports a hardware failure.",
            [7] = "Pairing took too long and stopped.",
            [8] = "Windows couldn't complete the pairing (code 8).",
            [9] = "Pairing failed when this PC and the device checked each other.",
            [10] = "Windows found nothing it can use on this device.",
            [11] = "Windows couldn't complete the pairing (code 11).",
            [12] = "Windows didn't allow Earshot to pair.",
            [13] = "Windows couldn't complete the pairing (code 13).",
            [14] = "Pairing was cancelled.",
            [15] = "Windows is already pairing a device. Try again in a moment.",
            [16] = "Windows couldn't complete the pairing (code 16).",
            [17] = "Pairing was cancelled.",
            [18] = "Windows says the device is already set up with something else.",
            [19] = "Windows couldn't pair the AirPods (code 19).",
        };

        foreach ((int code, string line) in expected)
        {
            Assert.AreEqual(line, PairReasons.LineFor(Status(code)), "status " + code);
        }
    }

    [TestMethod]
    public void AValueTheTableDoesNotKnowIsReportedAsItsHexCode()
    {
        Assert.AreEqual("Windows couldn't pair the AirPods (code 0x00000040).", PairReasons.LineFor(Status(64)));
    }

    [TestMethod]
    public void AThrownHResultIsReportedAsHexAndNeverReadAsAStatus()
    {
        // 0x80070005 as an int is negative; a thrown exception whose HRESULT happened to be small must not be read as a status.
        var denied = new PairingCallResult("COMException", unchecked((int)0x80070005), false, "HRESULT 0x80070005", Threw: true);
        var small = new PairingCallResult("InvalidOperationException", 5, false, null, Threw: true);

        Assert.AreEqual("Windows couldn't pair the AirPods (code 0x80070005).", PairReasons.LineFor(denied));
        Assert.AreEqual("Windows couldn't pair the AirPods (code 0x00000005).", PairReasons.LineFor(small));
        Assert.IsNull(PairReasons.StatusOf(small));
        Assert.IsFalse(PairReasons.IsSuccess(new PairingCallResult("x", 0, true, null, Threw: true)), "A thrown call never counts as paired.");
    }

    [TestMethod]
    public void OnlyPairedAndAlreadyPairedCountAsDone()
    {
        Assert.IsTrue(PairReasons.IsSuccess(Status(0)));
        Assert.IsTrue(PairReasons.IsSuccess(Status(3)));
        for (int code = 1; code <= 19; code++)
        {
            if (code != 3)
            {
                Assert.IsFalse(PairReasons.IsSuccess(Status(code)), "status " + code);
            }
        }
    }

    [TestMethod]
    public void NoLineAdvisesAnAppleResetOrUsesAnEmDash()
    {
        for (int code = 0; code <= 70; code++)
        {
            string line = PairReasons.LineFor(Status(code));
            Assert.IsFalse(line.Contains("reset", StringComparison.OrdinalIgnoreCase), "status " + code);
            Assert.IsFalse(line.Contains((char)0x2014), "status " + code);
            Assert.IsTrue(Regex.IsMatch(line, "^[A-Z]"), line);
        }
    }

    [TestMethod]
    public void TheInstructionHasBothCaseLinesAndTheWhiteLight()
    {
        Assert.Contains("double-tap the front of the case", PairingText.Instruction, StringComparison.Ordinal);
        Assert.Contains("hold the button on the back", PairingText.Instruction, StringComparison.Ordinal);
        Assert.Contains("AirPods 4", PairingText.CaseFront, StringComparison.Ordinal);
        Assert.AreEqual(2, Regex.Count(PairingText.Instruction, "flashes white"));
        Assert.AreEqual("Does your device show 123456?", PairingText.DoesItShow("123456"));
    }
}
