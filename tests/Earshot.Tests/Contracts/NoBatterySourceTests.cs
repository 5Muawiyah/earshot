using Earshot.Contracts;
using Earshot.Contracts.Null;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Contracts;

[TestClass]
public sealed class NoBatterySourceTests
{
    [TestMethod]
    public void HasNoSource()
    {
        Assert.IsFalse(new NoBatterySource().HasSource);
    }

    [TestMethod]
    public void ReadNeverHasAValue()
    {
        var source = new NoBatterySource();
        foreach (Guid container in new[] { Guid.Empty, NodeMatch.PcContainer, new Guid("1A2B3C4D-5E6F-5A7B-8C9D-0E1F2A3B4C5D") })
        {
            BatteryReading reading = source.Read(container);
            Assert.IsFalse(reading.HasValue);
            Assert.AreEqual(0, reading.Percent);
        }
    }
}
