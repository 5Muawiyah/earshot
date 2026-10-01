using System.Diagnostics;
using Earshot.Contracts;
using Earshot.Tests.Integration.Coordinator;
using Earshot.Tests.Phase1;
using Earshot.Tests.TestWindow;
using Earshot.Tray;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

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

    private static readonly string[] DisconnectThenBlock = ["disconnect", "block"];

    // The tray's side: the signal takes the menu's own exit path and ends the message loop, with the AirPods connected to this
    // PC. With Hand back on they are disconnected and then blocked; with it off Exit leaves them as they are, which is what the
    // menu's Exit does too. A second request while it is closing does nothing more.
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void TheSignalEndsTheTrayThroughTheMenusExitPathAndHandsTheAirPodsBackOnlyWhenHandBackIsOn(bool handBackOn)
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Devices.Active(1), exitWaitLimit: TimeSpan.FromSeconds(5),
                settings: s => s.HandBackOnShutdownAndSleep = handBackOn, arrange: t => t.Block.Status = Block(BlockState.Allowed));
            var order = new List<string>();
            tray.Connection.OnDisconnect = _ =>
            {
                order.Add("disconnect");
                return Task.FromResult(new ConnectResult(ConnectOutcome.Confirmed, "Disconnected", []));
            };
            tray.Block.OnBlock = _ =>
            {
                order.Add("block");
                return Task.FromResult(ControllerResult.Ok("Blocked at boot"));
            };
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
            if (handBackOn)
            {
                CollectionAssert.AreEqual(DisconnectThenBlock, order, "With Hand back on, --exit lets go of the AirPods and blocks them.");
                Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Hand-back (exit): finished in"));
            }
            else
            {
                Assert.IsEmpty(order, "With Hand back off, --exit leaves the AirPods connected, as the menu's Exit does.");
                Assert.IsEmpty(tray.Connection.Calls);
                Assert.IsEmpty(tray.Block.Calls);
                Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Hand-back: off, so nothing runs for this Exit."));
            }
        });
    }

    // The one line the test below cannot run: RunPrimaryTray wires the exit event under the tray's one fixed instance name, the
    // name its single-instance lock and the --exit command use, so only a real tray start could execute it, and a real start
    // here would take that lock or signal the owner's own tray when it is running. So this pins the source instead: it is a
    // pin and not a proof, and it fails when the call is removed, renamed to another instance, or moved after the message loop.
    [TestMethod]
    public void TheTraysStartWiresTheExitEventUnderTheInstanceNameItsLockAndTheExitCommandUseBeforeTheMessageLoopRuns()
    {
        string text = File.ReadAllText(Path.Combine(RepositoryLocator.RepositoryRoot(), "src", "Earshot", "App", "Program.Tray.cs"));
        int wired = text.IndexOf("exitSignal = WireExitSignal(log, TrayInstanceName, ui, shown);", StringComparison.Ordinal);
        int loop = text.IndexOf("Application.Run(context);", StringComparison.Ordinal);

        Assert.IsGreaterThan(-1, wired, "RunPrimaryTray no longer wires the exit event for the tray's own instance name.");
        Assert.IsGreaterThan(-1, loop);
        Assert.IsLessThan(loop, wired, "The exit event must be wired before the message loop runs.");
        StringAssert.Contains(text, "new Mutex(initiallyOwned: true, TrayInstanceName,", "The lock and the exit event use the same instance name.");
        StringAssert.Contains(text, "RequestTrayExit(log, TrayInstanceName)", "The --exit command asks the same instance.");
        StringAssert.Contains(text, "exitSignal?.Dispose()", "The wiring is let go when the tray ends.");
    }

    // The wiring the tray's start makes: a request for the instance sets the event, the registered wait posts the tray's own
    // exit on to the UI context, and the message loop ends. A watchdog ends the loop if the request never arrives, so a broken
    // wiring fails this test instead of hanging it.
    [TestMethod]
    public void ARequestToExitForTheInstanceReachesTheTraysOwnExitThroughTheWiringTheTrayStartMakes()
    {
        StaThread.Run(() =>
        {
            string instance = NewInstanceName();
            using var tray = new TrayHarness(snapshot: null, exitWaitLimit: TimeSpan.FromSeconds(5));
            using var watchdog = new Timer(_ => tray.Ui.Post(_ => System.Windows.Forms.Application.ExitThread(), null), null, TimeSpan.FromSeconds(20), Timeout.InfiniteTimeSpan);
            using IDisposable? wired = Program.WireExitSignal(tray.Log, instance, tray.Ui, tray.Context);
            Assert.IsNotNull(wired, "The exit event was not created.");

            Assert.IsTrue(Program.RequestTrayExit(tray.Log, instance));
            System.Windows.Forms.Application.Run(tray.Context);

            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Exit was asked for by another copy of Earshot (--exit)."), "The request did not reach the tray's Exit.");
        });
    }
}
