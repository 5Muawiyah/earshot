using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

[TestClass]
public sealed class BatteryNibbleTests
{
    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(1, 10)]
    [DataRow(5, 50)]
    [DataRow(9, 90)]
    [DataRow(10, 100)]
    public void ZeroToTenAreTensOfPercent(int nibble, int expectedPercent)
    {
        Assert.AreEqual(expectedPercent, BatteryNibble.ToPercent(nibble));
        Assert.IsFalse(BatteryNibble.IsOutOfRange(nibble));
    }

    [TestMethod]
    public void FifteenIsUnknown()
    {
        Assert.IsNull(BatteryNibble.ToPercent(0xF));
        Assert.IsFalse(BatteryNibble.IsOutOfRange(0xF));
    }

    [TestMethod]
    [DataRow(11)]
    [DataRow(12)]
    [DataRow(13)]
    [DataRow(14)]
    public void ElevenToFourteenAreUnknownAndOutOfRange(int nibble)
    {
        Assert.IsNull(BatteryNibble.ToPercent(nibble));
        Assert.IsTrue(BatteryNibble.IsOutOfRange(nibble));
    }
}
