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
            [1] = "The device isn't in a state where it can be paired.",
            [2] = "Windows didn't pair the device.",
            [3] = "Paired.",
            [4] = "The device turned down the connection.",
            [5] = "The device can't accept any more connections.",
            [6] = "The device reported a hardware failure.",
            [7] = "Pairing took too long and stopped.",
            [8] = "Windows couldn't complete the pairing (code 8).",
            [9] = "Pairing failed because the authentication was turned down.",
            [10] = "Windows found no profiles this device can use.",
            [11] = "Windows couldn't complete the pairing (code 11).",
            [12] = "Windows didn't allow Earshot to pair.",
            [13] = "Windows couldn't complete the pairing (code 13).",
            [14] = "Pairing was cancelled.",
            [15] = "The device is already pairing or unpairing. Try again in a moment.",
            [16] = "Windows couldn't complete the pairing (code 16).",
            [17] = "Pairing was cancelled.",
            [18] = "The device already has an association.",
            [19] = "Windows couldn't pair the device (code 19).",
        };

        foreach ((int code, string line) in expected)
        {
            Assert.AreEqual(line, PairReasons.LineFor(Status(code)), "status " + code);
        }
    }

    [TestMethod]
    public void AValueTheTableDoesNotKnowIsReportedAsItsHexCode()
    {
        Assert.AreEqual("Windows couldn't pair the device (code 0x00000040).", PairReasons.LineFor(Status(64)));
    }

    [TestMethod]
    public void AThrownHResultIsReportedAsHexAndNeverReadAsAStatus()
    {
        // 0x80070005 as an int is negative; a thrown exception whose HRESULT happened to be small must not be read as a status.
        var denied = new PairingCallResult("COMException", unchecked((int)0x80070005), false, "HRESULT 0x80070005", Threw: true);
        var small = new PairingCallResult("InvalidOperationException", 5, false, null, Threw: true);

        Assert.AreEqual("Windows couldn't pair the device (code 0x80070005).", PairReasons.LineFor(denied));
        Assert.AreEqual("Windows couldn't pair the device (code 0x00000005).", PairReasons.LineFor(small));
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
