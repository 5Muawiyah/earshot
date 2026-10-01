using System.Diagnostics;
using Earshot.Contracts;
using Earshot.Tests.Phase1;
using Earshot.Tray;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.App;

// Earshot.exe --exit: a second copy asks the running tray to exit by the menu's own path and ends. The signal is a named
// event beside the show event; every test here names its own instance, so none can reach a real tray, and none starts the
// program with --exit (the owner's tray may be running).
[TestClass]
public sealed class TrayExitCommandTests
{
    private static string NewInstanceName() => "Earshot.Tests." + Guid.NewGuid().ToString("N");

    private static EventWaitHandle ExitEventFor(string instance) =>
        new(false, EventResetMode.AutoReset, Program.ExitEventName(instance), Program.TrayInstanceOptions, out _);

    [TestMethod]
    public void TheExitArgumentAloneIsAValidTrayCommandLine()
    {
        Assert.IsTrue(Program.IsTrayCommandLine(["--exit"], out string? error), error);
        Assert.IsTrue(Program.IsTrayCommandLine([], out _));
        Assert.IsTrue(Program.IsTrayCommandLine(["--startup"], out _));

        Assert.IsFalse(Program.IsTrayCommandLine(["--exit", "--startup"], out string? withMore));
        StringAssert.Contains(withMore, "--exit takes no further arguments.");
        Assert.IsFalse(Program.IsTrayCommandLine(["--startup", "--exit"], out string? startupFirst));
        StringAssert.Contains(startupFirst, "--startup takes no further arguments.");
        Assert.IsFalse(Program.IsTrayCommandLine(["--exitt"], out _));
        StringAssert.Contains(Program.TrayUsage, "--exit");
    }

    [TestMethod]
    public void TheExitEventIsNamedBesideTheShowEvent()
    {
        Assert.AreEqual("Earshot.x.exit", Program.ExitEventName("Earshot.x"));
        Assert.AreEqual("Earshot.x.show", Program.ShowEventName("Earshot.x"));
        Assert.AreNotEqual(Program.ShowEventName(Program.TrayInstanceName), Program.ExitEventName(Program.TrayInstanceName));
    }

    [TestMethod]
    public void AskingARunningTrayToExitSetsItsEventAndSaysSo()
    {
        string instance = NewInstanceName();
        using EventWaitHandle exitEvent = ExitEventFor(instance);
        var log = new CapturingLog();

        bool asked = Program.RequestTrayExit(log, instance);

        Assert.IsTrue(asked);
        Assert.IsTrue(exitEvent.WaitOne(0), "The event the tray waits on was set.");
        Assert.IsTrue(log.Has(LogLevel.Info, "asked to exit"));
    }

    [TestMethod]
    public void AskingWhenNoTrayIsRunningFindsNoEventAndSaysSoWithoutCreatingOne()
    {
        string instance = NewInstanceName();
        var log = new CapturingLog();

        bool asked = Program.RequestTrayExit(log, instance);

        Assert.IsFalse(asked);
        Assert.IsTrue(log.Has(LogLevel.Warn, "Earshot is not running"));
        Assert.IsFalse(EventWaitHandle.TryOpenExisting(Program.ExitEventName(instance), Program.TrayInstanceOptions, out _), "Asking must not make the event a tray would later find.");
    }

    [TestMethod]
    public void TheRegisteredWaitCallsBackOncePerRequestAndNotAfterItIsUnregistered()
    {
        string instance = NewInstanceName();
        using EventWaitHandle exitEvent = ExitEventFor(instance);
        using var called = new SemaphoreSlim(0);
        int count = 0;
        RegisteredWaitHandle wait = Program.RegisterExitWait(exitEvent, () =>
        {
            Interlocked.Increment(ref count);
            called.Release();
        });

        Assert.IsTrue(Program.RequestTrayExit(new CapturingLog(), instance));
        Assert.IsTrue(called.Wait(TimeSpan.FromSeconds(10)), "The callback did not run.");
        Assert.IsFalse(called.Wait(TimeSpan.FromMilliseconds(200)), "One request, one callback.");

        using var unregistered = new ManualResetEvent(false);
        if (wait.Unregister(unregistered))
        {
            Assert.IsTrue(unregistered.WaitOne(TimeSpan.FromSeconds(10)));
        }

        Assert.IsTrue(Program.RequestTrayExit(new CapturingLog(), instance));
        Assert.IsFalse(called.Wait(TimeSpan.FromMilliseconds(300)), "Nothing is called after the wait is unregistered.");
        Assert.AreEqual(1, count);
    }

    // The tray's side: the signal takes the menu's own exit path and ends the message loop, with the hand-back setting on
    // or off, and a second request while it is closing does nothing more.
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void TheSignalEndsTheTrayThroughTheMenusExitPath(bool handBackOn)
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: null, exitWaitLimit: TimeSpan.FromSeconds(5),
                settings: s => s.HandBackOnShutdownAndSleep = handBackOn);
            tray.Ui.Post(_ =>
            {
                tray.Context.ExitFromSignal();
                tray.Context.ExitFromSignal();
            }, null);
            var watch = Stopwatch.StartNew();

            System.Windows.Forms.Application.Run(tray.Context);

            Assert.IsLessThan(TimeSpan.FromSeconds(10), watch.Elapsed, "The tray did not end.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Exit was asked for by another copy of Earshot (--exit)."));
            Assert.AreEqual(1, tray.Log.Entries.Count(e => e.Message.Contains("--exit)", StringComparison.Ordinal)), "The second request did nothing.");
        });
    }
}
