using Earshot.AudioProtection.Gate;
using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase6;

// The device change lock over a temporary folder. Nothing else is locked and nothing waits in real time.
[TestClass]
public sealed class DeviceChangeLockTests
{
    private static bool NoWait(TimeSpan _) => true;

    private static bool CanOpenForWrite(string folder)
    {
        try
        {
            using var stream = new FileStream(Path.Combine(folder, DeviceChangeLock.FileName), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    [TestMethod]
    public void TheLockIsHeldUntilDisposed()
    {
        using var temp = new TempFolder();
        var steps = new List<StepOutcome>();

        DeviceChangeLock? held = DeviceChangeLock.TryAcquire(temp.Path, TimeSpan.Zero, NoWait, steps);

        Assert.IsNotNull(held);
        Assert.IsTrue(steps.Single().Ok);
        Assert.IsFalse(CanOpenForWrite(temp.Path), "Another writer is kept out.");
        held.Dispose();
        Assert.IsTrue(CanOpenForWrite(temp.Path));
    }

    [TestMethod]
    public void TheSameThreadCanTakeItAgainAndOnlyTheOutermostReleaseFreesIt()
    {
        using var temp = new TempFolder();
        var steps = new List<StepOutcome>();

        using DeviceChangeLock? outer = DeviceChangeLock.TryAcquire(temp.Path, TimeSpan.Zero, NoWait, steps);
        DeviceChangeLock? inner = DeviceChangeLock.TryAcquire(temp.Path, TimeSpan.Zero, NoWait, steps);
        Assert.IsNotNull(outer);
        Assert.IsNotNull(inner);
        inner.Dispose();

        Assert.IsFalse(CanOpenForWrite(temp.Path), "Still held by the outer caller.");
        Assert.HasCount(1, steps, "Taking it again opens nothing.");
        outer.Dispose();
        Assert.IsTrue(CanOpenForWrite(temp.Path));
    }

    [TestMethod]
    public async Task AnotherThreadWaitsAndGivesUpWithABusyStep()
    {
        using var temp = new TempFolder();
        using DeviceChangeLock? held = DeviceChangeLock.TryAcquire(temp.Path, TimeSpan.Zero, NoWait, new List<StepOutcome>());
        Assert.IsNotNull(held);
        var steps = new List<StepOutcome>();
        int waits = 0;

        DeviceChangeLock? other = await Task.Factory.StartNew(
            () => DeviceChangeLock.TryAcquire(temp.Path, TimeSpan.FromMilliseconds(500), _ => ++waits > 0, steps),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        Assert.IsNull(other);
        Assert.AreEqual(2, waits);
        StepOutcome busy = steps.Single();
        Assert.IsTrue(DeviceChangeLock.IsBusy(busy));
        Assert.AreEqual("ERROR_SHARING_VIOLATION", busy.CodeName);
        Assert.Contains("still running after 0.5 s", busy.Detail!);
    }

    [TestMethod]
    public void AWaitThatStopsGivesUpAtOnce()
    {
        using var temp = new TempFolder();
        using var other = new FileStream(Path.Combine(temp.Path, DeviceChangeLock.FileName), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        var steps = new List<StepOutcome>();
        int waits = 0;

        DeviceChangeLock? held = DeviceChangeLock.TryAcquire(temp.Path, TimeSpan.FromMinutes(5), _ => ++waits < 0, steps);

        Assert.IsNull(held);
        Assert.AreEqual(1, waits);
        Assert.IsTrue(DeviceChangeLock.IsBusy(steps.Single()));
        Assert.Contains("stopped waiting", steps.Single().Detail!);
    }

    [TestMethod]
    public void AnOpenThatFailsForAnotherReasonIsRecordedAndNotRetried()
    {
        using var temp = new TempFolder();
        string missing = Path.Combine(temp.Path, "missing");
        var steps = new List<StepOutcome>();
        int waits = 0;

        DeviceChangeLock? held = DeviceChangeLock.TryAcquire(missing, TimeSpan.FromMinutes(5), _ => ++waits > 0, steps);

        Assert.IsNull(held);
        Assert.AreEqual(0, waits);
        StepOutcome failed = steps.Single();
        Assert.IsFalse(failed.Ok);
        Assert.IsFalse(DeviceChangeLock.IsBusy(failed), "A missing folder is not another change holding the lock.");
        Assert.AreEqual("ERROR_PATH_NOT_FOUND", failed.CodeName);
    }

    [TestMethod]
    public void OnlyAFailedLockStepIsBusy()
    {
        Assert.IsFalse(DeviceChangeLock.IsBusy(StepOutcomes.FromWin32(DeviceChangeLock.StepName, 32, ok: true)));
        Assert.IsFalse(DeviceChangeLock.IsBusy(StepOutcomes.FromWin32("write-protection-intent", 32)));
        Assert.IsTrue(DeviceChangeLock.IsBusy(StepOutcomes.FromWin32(DeviceChangeLock.StepName, 32)));
    }
}
