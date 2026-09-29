using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// What the watcher reports when the reader itself throws.
[TestClass]
public sealed class TaskbarWatcherFailureTests
{
    private sealed class ThrowingReader : ITaskbarReader
    {
        public ITaskbarReader.Result Read(ShownGauge? shownGauge) => throw new InvalidOperationException("the read threw");
    }

    // A thrown read says nothing about whether the taskbar is there. It used to be reported as "no taskbar",
    // the one failure that hides the gauge at once, so a single exception made the gauge vanish.
    [TestMethod]
    public void AThrowingReadReportsTheExceptionStepNotNoTaskbar()
    {
        ITaskbarReader.Result? delivered = null;
        using var delivery = new ManualResetEventSlim();
        using var watcher = new TaskbarWatcher(
            new ThrowingReader(), () => null, result =>
            {
                delivered = result;
                delivery.Set();
            }, action => action(), new CapturingLog(), TimeProvider.System)
        {
            PollIntervalMs = 30000,
        };

        watcher.Start();
        watcher.Poke();

        Assert.IsTrue(delivery.Wait(TimeSpan.FromSeconds(5)), "The failure must still reach the UI thread.");
        Assert.IsNotNull(delivered!.Value.Failure);
        Assert.AreEqual(TaskbarReadFailureStep.Exception, delivered.Value.Failure!.Step);
    }
}
