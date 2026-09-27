using Earshot.Widget.Alert;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

[TestClass]
public sealed class LowBatteryLatchTests
{
    [TestMethod]
    public void FiresWhenAPartFirstReadsAtOrBelowTheThreshold()
    {
        var latch = new LowBatteryLatch(20);

        Assert.IsTrue(latch.ApplyLeft(20));
        Assert.AreEqual(LatchState.Fired, latch.Left);

        var another = new LowBatteryLatch(20);
        Assert.IsFalse(another.ApplyLeft(30));
        Assert.AreEqual(LatchState.Armed, another.Left);
    }

    [TestMethod]
    public void DoesNotRepeatWhileHoveringAtTheBoundary()
    {
        var latch = new LowBatteryLatch(20);
        int fires = 0;

        if (latch.ApplyLeft(20)) fires++;
        if (latch.ApplyLeft(20)) fires++;
        if (latch.ApplyLeft(10)) fires++;

        Assert.AreEqual(1, fires);
    }

    [TestMethod]
    public void ReArmsOnlyOneStepAbove()
    {
        var latch = new LowBatteryLatch(20);
        int fires = 0;
        foreach (int reading in new[] { 20, 30, 20 })
        {
            if (latch.ApplyLeft(reading)) fires++;
        }

        Assert.AreEqual(2, fires);

        var latch2 = new LowBatteryLatch(20);
        int fires2 = 0;
        foreach (int reading in new[] { 20, 20, 30, 30, 20 })
        {
            if (latch2.ApplyLeft(reading)) fires2++;
        }

        Assert.AreEqual(2, fires2);
    }

    [TestMethod]
    public void UnknownChangesNothing()
    {
        var latch = new LowBatteryLatch(20);

        Assert.IsFalse(latch.ApplyLeft(null));
        Assert.AreEqual(LatchState.Armed, latch.Left);

        latch.ApplyLeft(20);
        Assert.AreEqual(LatchState.Fired, latch.Left);
        Assert.IsFalse(latch.ApplyLeft(null));
        Assert.AreEqual(LatchState.Fired, latch.Left);
    }

    [TestMethod]
    public void EachPartIsItsOwnLatch()
    {
        var latch = new LowBatteryLatch(20);

        latch.ApplyLeft(20);

        Assert.AreEqual(LatchState.Fired, latch.Left);
        Assert.AreEqual(LatchState.Armed, latch.Right);
        Assert.AreEqual(LatchState.Armed, latch.Case);
    }

    [TestMethod]
    public void AThresholdChangeReArmsEveryLatch()
    {
        var latch = new LowBatteryLatch(20);
        latch.ApplyLeft(20);
        latch.ApplyRight(20);
        latch.ApplyCase(20);
        Assert.AreEqual(LatchState.Fired, latch.Left);
        Assert.AreEqual(LatchState.Fired, latch.Right);
        Assert.AreEqual(LatchState.Fired, latch.Case);

        latch.SetThreshold(10);

        Assert.AreEqual(LatchState.Armed, latch.Left);
        Assert.AreEqual(LatchState.Armed, latch.Right);
        Assert.AreEqual(LatchState.Armed, latch.Case);
        Assert.AreEqual(10, latch.ThresholdPercent);
    }

    [TestMethod]
    public void TheDefaultThresholdIsTwenty()
    {
        var latch = new LowBatteryLatch();

        Assert.AreEqual(20, latch.ThresholdPercent);
    }
}
