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
        foreach (Guid container in new[] { Guid.Empty, NodeMatch.PcContainer, new Guid("5C3A9E21-4B7D-5F18-9A6C-2D8E0B4F7A13") })
        {
            BatteryReading reading = source.Read(container);
            Assert.IsFalse(reading.HasValue);
            Assert.IsNull(reading.Percent, "No figure is made up for a source that has none.");
        }
    }
}
