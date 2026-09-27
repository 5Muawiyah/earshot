using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

[TestClass]
public sealed class ProximityParserTests
{
    private const ushort Apple = ProximityParser.AppleCompanyId;
    private const ushort NotApple = 0x0006;

    [TestMethod]
    public void WrongCompanyReadsNothing()
    {
        byte[] data = WidgetFixtures.Proximity();

        ProximityParse parse = ProximityParser.Parse(NotApple, data);

        Assert.AreEqual(ProximityParseStatus.WrongCompany, parse.Status);
        Assert.IsNull(parse.Message);
        Assert.AreEqual(0, parse.ProximityItems);
        Assert.AreEqual(0, parse.TypesSeen.Count);
    }

    [TestMethod]
    public void AppleDataWithoutAProximityItemIsWrongTypeAndTheTypesAreListed()
    {
        byte[] data = WidgetFixtures.Concat(
            WidgetFixtures.Item(0x02, new byte[] { 0x01, 0x02 }),
            WidgetFixtures.Item(0x03, new byte[] { 0x03 }));

        ProximityParse parse = ProximityParser.Parse(Apple, data);

        Assert.AreEqual(ProximityParseStatus.WrongType, parse.Status);
        Assert.IsNull(parse.Message);
        Assert.AreEqual(0, parse.ProximityItems);
        CollectionAssert.AreEqual(new byte[] { 0x02, 0x03 }, (System.Collections.ICollection)parse.TypesSeen);
    }

    [TestMethod]
    public void AHeaderCutShortIsTruncated()
    {
        byte[] data = new byte[] { 0x07 }; // only the type byte, no length

        ProximityParse parse = ProximityParser.Parse(Apple, data);

        Assert.AreEqual(ProximityParseStatus.Truncated, parse.Status);
        Assert.IsNull(parse.Message);
    }

    [TestMethod]
    public void AnItemRunningPastTheDataIsTruncated()
    {
        // Declares 25 bytes of value but the buffer holds fewer.
        byte[] data = new byte[] { 0x07, 0x19, 0x01, 0x02, 0x03 };

        ProximityParse parse = ProximityParser.Parse(Apple, data);

        Assert.AreEqual(ProximityParseStatus.Truncated, parse.Status);
        Assert.IsNull(parse.Message);
    }

    [TestMethod]
    public void ADocumentedFormCutShortIsTruncatedWithItsLength()
    {
        byte[] shortValue = new byte[] { 0x01, 0x02, 0x03 };
        byte[] data = WidgetFixtures.Item(0x07, shortValue);

        ProximityParse parse = ProximityParser.Parse(Apple, data);

        Assert.AreEqual(ProximityParseStatus.Truncated, parse.Status);
        Assert.AreEqual((byte)0x01, parse.Prefix);
        Assert.AreEqual(3, parse.Length);
        Assert.IsNull(parse.Message);
    }

    [TestMethod]
    public void TheSeventeenByteZeroSixFormIsUnknownFormWithPrefixAndLengthAndNoFields()
    {
        byte[] data = WidgetFixtures.UnknownSeventeenByteForm();

        ProximityParse parse = ProximityParser.Parse(Apple, data);

        Assert.AreEqual(ProximityParseStatus.UnknownForm, parse.Status);
        Assert.AreEqual((byte)0x06, parse.Prefix);
        Assert.AreEqual(17, parse.Length);
        Assert.IsNull(parse.Message);
    }

    [TestMethod]
    public void ALongerZeroOneFormIsUnknownForm()
    {
        byte[] longValue = new byte[26];
        longValue[0] = 0x01;
        byte[] data = WidgetFixtures.Item(0x07, longValue);

        ProximityParse parse = ProximityParser.Parse(Apple, data);

        Assert.AreEqual(ProximityParseStatus.UnknownForm, parse.Status);
        Assert.AreEqual((byte)0x01, parse.Prefix);
        Assert.AreEqual(26, parse.Length);
        Assert.IsNull(parse.Message);
    }

    [TestMethod]
    public void AZeroLengthProximityItemIsUnknownForm()
    {
        byte[] data = WidgetFixtures.Item(0x07, Array.Empty<byte>());

        ProximityParse parse = ProximityParser.Parse(Apple, data);

        Assert.AreEqual(ProximityParseStatus.UnknownForm, parse.Status);
        Assert.IsNull(parse.Prefix);
        Assert.AreEqual(0, parse.Length);
        Assert.IsNull(parse.Message);
    }

    [TestMethod]
    public void TheDocumentedFormReadsEveryFieldAtItsOffset()
    {
        byte[] data = WidgetFixtures.Proximity(
            modelHigh: 0x11, modelLow: 0x22, status: 0x33, batteryA: 0x44, batteryB: 0x55, lid: 0x66,
            colour: 0x77, reserved: 0x00);

        ProximityParse parse = ProximityParser.Parse(Apple, data);

        Assert.AreEqual(ProximityParseStatus.Ok, parse.Status);
        Assert.IsNotNull(parse.Message);
        ProximityMessage message = parse.Message!.Value;
        Assert.AreEqual((byte)0x11, message.ModelHigh);
        Assert.AreEqual((byte)0x22, message.ModelLow);
        Assert.AreEqual((byte)0x33, message.Status);
        Assert.AreEqual((byte)0x44, message.BatteryA);
        Assert.AreEqual((byte)0x55, message.BatteryB);
        Assert.AreEqual((byte)0x66, message.Lid);
        Assert.AreEqual((byte)0x77, message.Colour);
        Assert.AreEqual((byte)0x00, message.Reserved);
        Assert.IsNull(parse.Prefix);
        Assert.IsNull(parse.Length);
        Assert.AreEqual(1, parse.ProximityItems);
    }

    [TestMethod]
    public void TheEncryptedBytesAreNotInTheResult()
    {
        // ProximityMessage carries exactly the documented fields; there is no member the 16 encrypted
        // bytes could have been copied into.
        string[] names = typeof(ProximityMessage).GetProperties().Select(p => p.Name).ToArray();
        string[] expected = { "BatteryA", "BatteryB", "Colour", "Lid", "ModelHigh", "ModelLow", "Reserved", "Status" };
        Array.Sort(names, StringComparer.Ordinal);
        Array.Sort(expected, StringComparer.Ordinal);

        CollectionAssert.AreEqual(expected, names);
    }

    [TestMethod]
    public void TheFirstProximityItemDecidesAndTheSecondIsCounted()
    {
        byte[] first = WidgetFixtures.Proximity(modelHigh: 0x01);
        byte[] second = WidgetFixtures.Proximity(modelHigh: 0x02);
        byte[] data = WidgetFixtures.Concat(first, second);

        ProximityParse parse = ProximityParser.Parse(Apple, data);

        Assert.AreEqual(ProximityParseStatus.Ok, parse.Status);
        Assert.AreEqual((byte)0x01, parse.Message!.Value.ModelHigh);
        Assert.AreEqual(2, parse.ProximityItems);
    }

    [TestMethod]
    public void OtherItemsBeforeTheProximityItemAreSkipped()
    {
        byte[] data = WidgetFixtures.Concat(
            WidgetFixtures.Item(0x02, new byte[] { 0x01 }),
            WidgetFixtures.Proximity());

        ProximityParse parse = ProximityParser.Parse(Apple, data);

        Assert.AreEqual(ProximityParseStatus.Ok, parse.Status);
        CollectionAssert.AreEqual(new byte[] { 0x02 }, (System.Collections.ICollection)parse.TypesSeen);
    }
}
