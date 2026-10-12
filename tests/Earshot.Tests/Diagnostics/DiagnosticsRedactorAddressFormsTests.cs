using Earshot.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Diagnostics;

// A 48-bit Bluetooth address is printed as 12 hex digits, in 64 bits as 16 hex digits with the unused bits zero, and by some
// code as a plain decimal integer. Each form is planted here in a line of log text; none may survive, and the numbers that are
// not addresses must.
[TestClass]
public sealed class DiagnosticsRedactorAddressFormsTests
{
    private const string User = "jbloggs";
    private const string Hex12 = "A1B2C3D4E5F6";

    private static string Redact(string text) => DiagnosticsRedactor.Redact(text, null, User);

    [TestMethod]
    [DataRow("0000A1B2C3D4E5F6")]
    [DataRow("0x0000A1B2C3D4E5F6")]
    [DataRow("0000a1b2c3d4e5f6")]
    [DataRow("A1B2C3D4E5F60000")]
    [DataRow("0xa1b2c3d4e5f60000")]
    public void AnAddressHeldInSixtyFourBitsIsTaken(string form)
    {
        Assert.AreEqual("addr <address> end", Redact("addr " + form + " end"), form);
    }

    [TestMethod]
    public void AnAddressWrittenAsADecimalIntegerIsTaken()
    {
        long value = Convert.ToInt64(Hex12, 16);
        string decimalForm = value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        Assert.AreEqual(15, decimalForm.Length, "The planted address is a 15-digit integer.");
        Assert.AreEqual("addr <address> end", Redact("addr " + decimalForm + " end"));
        Assert.AreEqual("address=<address>.", Redact("address=" + decimalForm + "."));
        Assert.AreEqual("<address>", Redact(decimalForm));
        Assert.AreEqual("<address>", Redact(0xFFFFFFFFFFFF.ToString(System.Globalization.CultureInfo.InvariantCulture)), "The largest 48-bit address.");
        Assert.AreEqual("<address>", Redact("11111822610060"), "A 14-digit address.");
        Assert.AreEqual("<address>", Redact("1099511627776"), "A 13-digit address.");
    }

    [TestMethod]
    public void NumbersThatCannotBeAddressesOrBelongToSomethingElseAreKept()
    {
        foreach (string text in new[]
        {
            "281474976710656",                      // one more than the largest 48-bit address
            "999999999999999",                      // 15 digits, too large
            "1234567890123456",                     // 16 digits
            "settings.corrupt-20261002091530.json", // a compact date joined by "-" is part of a file name
            "version 10.0.26100.1760000000000",     // joined by "." to a version
            "123456789ABCDEF0",                     // 16 hex digits with no zero edge
            "7FF6A1B2C3D4E5F6",
            "ticks 12345",
        })
        {
            Assert.AreEqual(text, Redact(text), text);
        }
    }

    [TestMethod]
    public void EveryFormIsRemovedFromALogLineAndTheHarmlessPartsStay()
    {
        long value = Convert.ToInt64(Hex12, 16);
        string line = "2026-10-12T09:15:30.123Z INFO seen " + Hex12 + " also 0000" + Hex12 + " and " + value + " code 1460";

        string result = Redact(line);

        Assert.AreEqual("2026-10-12T09:15:30.123Z INFO seen <address> also <address> and <address> code 1460", result);
        Assert.AreEqual(result, Redact(result), "Redacting again changes nothing.");
    }
}
