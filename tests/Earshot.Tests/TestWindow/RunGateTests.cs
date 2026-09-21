using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// Exactly one child may exist; a reply routes by identity and is dropped if
// its runner is no longer the active one.
[TestClass]
public sealed class RunGateTests
{
    [TestMethod]
    public void CanStartIsTrueWithNoActiveRunner()
    {
        Assert.IsTrue(RunGate.CanStart(null));
    }

    [TestMethod]
    public void CanStartIsFalseWhenSomethingIsAlreadyActive()
    {
        Assert.IsFalse(RunGate.CanStart(new object()));
    }

    [TestMethod]
    public void AMessageFromTheActiveRunnerIsProcessed()
    {
        var runner = new object();
        Assert.IsTrue(RunGate.ShouldProcessMessage(runner, runner));
    }

    [TestMethod]
    public void AMessageFromAnyOtherRunnerIsDropped()
    {
        var active = new object();
        var stale = new object();
        Assert.IsFalse(RunGate.ShouldProcessMessage(active, stale));
    }

    [TestMethod]
    public void AMessageArrivingAfterTheActiveRunnerIsClearedIsDropped()
    {
        var endedRunner = new object();
        Assert.IsFalse(RunGate.ShouldProcessMessage(null, endedRunner));
    }
}
