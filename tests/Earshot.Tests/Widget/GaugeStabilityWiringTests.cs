using System.Drawing;
using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Tests.Phase1;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Widget;

// The tray's side of the other displays' gauges: Explorer creating its taskbar again and the displays changing reach the set,
// which looks at once and again a moment later, and the main gauge's confirming reads reach the watcher. The tray runs on a
// private desktop with the displays, the secondary taskbar windows and the gauge windows replaced. The main taskbar is read once
// (the poll is made far longer than the test), so what is seen afterwards came from the signal and not from a poll.
[TestClass]
public sealed class GaugeStabilityWiringTests
{
    private sealed class MutableDisplays(params DisplayInfo[] displays) : IDisplaySource
    {
        private readonly Lock _gate = new();
        private DisplayInfo[] _displays = displays;

        public void Set(params DisplayInfo[] displays)
        {
            lock (_gate)
            {
                _displays = displays;
            }
        }

        public DisplayReading Read()
        {
            lock (_gate)
            {
                return new DisplayReading([.. _displays]);
            }
        }
    }

    private sealed class Taskbars
    {
        private readonly Lock _gate = new();
        private TaskbarWindowCandidate[] _bars = [];

        public void Set(params TaskbarWindowCandidate[] bars)
        {
            lock (_gate)
            {
                _bars = bars;
            }
        }

        public SecondaryTaskbarReading Read()
        {
            lock (_gate)
            {
                return new SecondaryTaskbarReading([.. _bars]);
            }
        }
    }

    private static TaskbarWindowCandidate BarOnTwo() =>
        new(0x5000, new Rectangle(ScriptedSet.Two.Bounds.X, ScriptedSet.Two.Bounds.Bottom - 48, ScriptedSet.Two.Bounds.Width, 48), true, ScriptedSet.Two.Handle);

    private static TrayHarness StartTray(MutableDisplays displays, Taskbars taskbars) =>
        new(
            snapshot: Target(ConnectionState.Disconnected), displaySource: displays, taskbarWatcherPollIntervalMs: 600_000,
            gaugeSurfaceFactory: () => new FakeGaugeSurface(), foregroundWindowProbe: _ => null, secondaryTaskbars: taskbars.Read,
            settings: s => s.Widget = s.Widget with { Enabled = true, ShowOnTaskbar = true, LeftClickConnects = false, GaugeDisplay = GaugeDisplayChoice.AllDisplays });

    private static void Dispatch(TrayHarness tray, int message)
    {
        var m = Message.Create(tray.Context.Window.Handle, message, 0, 0);
        tray.Context.Window.Dispatch(ref m);
    }

    // Reads the main taskbar once (a setting saved pokes the watcher) and waits for the tray to have handled the read, which shows the
    // main gauge and matches the other displays' gauges to what is there.
    private static void ReadMainTaskbar(TrayHarness tray)
    {
        int reads = tray.TaskbarReaders[0].ReadCount;
        tray.TaskbarReaders[0].SetNextResult(ITaskbarReader.Result.Ok(StabilityLayouts.Free() with { DisplayCount = 2 }));
        tray.Settings.Update(s => s.Widget = s.Widget with { LeftClickConnects = !s.Widget.LeftClickConnects });
        TrayHarness.PumpUntil(() => tray.TaskbarReaders[0].ReadCount > reads, "The main taskbar was not read.");
        TrayHarness.PumpUntil(() => tray.Context.WidgetGaugeStateForTest is GaugeState.Shown, "The main gauge was never shown.");
        PumpFor(TimeSpan.FromMilliseconds(100));
    }

    private static void PumpFor(TimeSpan span)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < span)
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }
    }

    [TestMethod]
    public void AChangeOfDisplaysLooksForTheOtherDisplaysTaskbarAtOnce()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var displays = new MutableDisplays(ScriptedSet.One, ScriptedSet.Two);
            var taskbars = new Taskbars();
            using TrayHarness tray = StartTray(displays, taskbars);
            tray.PumpUntilIdle();
            ReadMainTaskbar(tray);
            Assert.AreEqual(0, tray.Context.SecondaryGaugeCountForTest, "Sanity: the second display shows no taskbar yet.");

            taskbars.Set(BarOnTwo());
            Dispatch(tray, 0x007E);
            Assert.AreEqual(1, tray.Context.SecondaryGaugeCountForTest, "WM_DISPLAYCHANGE looks at once.");
        });
    }

    [TestMethod]
    public void ATaskbarExplorerBuildsAfterTheDisplaysChangedIsFoundByTheLookTwoSecondsLater()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var displays = new MutableDisplays(ScriptedSet.One, ScriptedSet.Two);
            var taskbars = new Taskbars();
            using TrayHarness tray = StartTray(displays, taskbars);
            tray.PumpUntilIdle();
            ReadMainTaskbar(tray);

            // The signal also pokes the main taskbar's watcher; its read is handled before the taskbar appears, so what finds the
            // taskbar afterwards is the signal's own follow-up and not that read.
            int reads = tray.TaskbarReaders[0].ReadCount;
            Dispatch(tray, 0x007E);
            TrayHarness.PumpUntil(() => tray.TaskbarReaders[0].ReadCount > reads, "The signal did not poke the main taskbar's watcher.");
            PumpFor(TimeSpan.FromMilliseconds(100));
            Assert.AreEqual(0, tray.Context.SecondaryGaugeCountForTest, "Nothing yet at the signal.");

            taskbars.Set(BarOnTwo());
            tray.Time.Advance(TimeSpan.FromSeconds(1));
            PumpFor(TimeSpan.FromMilliseconds(100));
            Assert.AreEqual(0, tray.Context.SecondaryGaugeCountForTest, "One second after the signal there has been no look.");
            tray.Time.Advance(TimeSpan.FromSeconds(1));
            TrayHarness.PumpUntil(() => tray.Context.SecondaryGaugeCountForTest == 1, "The look two seconds after the signal did not find the taskbar.");
        });
    }

    [TestMethod]
    public void ExplorerCreatingItsTaskbarAgainLooksForTheOtherDisplaysTaskbarAtOnce()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var displays = new MutableDisplays(ScriptedSet.One, ScriptedSet.Two);
            var taskbars = new Taskbars();
            using TrayHarness tray = StartTray(displays, taskbars);
            tray.PumpUntilIdle();
            ReadMainTaskbar(tray);
            Assert.AreEqual(0, tray.Context.SecondaryGaugeCountForTest);

            taskbars.Set(BarOnTwo());
            uint taskbarCreated = Shell.RegisterWindowMessage(Shell.TaskbarCreatedMessageName);
            Dispatch(tray, unchecked((int)taskbarCreated));

            Assert.AreEqual(1, tray.Context.SecondaryGaugeCountForTest, "TaskbarCreated looks at once.");
        });
    }
}
