using Earshot.Commands;
using Earshot.Contracts;
using Earshot.Infra;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Commands;

// A running tray holds a store and a watcher on settings.json. Another program (the settings command) saves through a store of
// its own. The tray's store must raise Changed with the new values, and must not raise it again for its own saves.
[TestClass]
public sealed class SettingsLiveReloadTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(100);

    private static EarshotSettings? WaitForChange(List<EarshotSettings> seen, Func<EarshotSettings, bool> predicate)
    {
        DateTime limit = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < limit)
        {
            lock (seen)
            {
                EarshotSettings? hit = seen.FirstOrDefault(predicate);
                if (hit is not null)
                {
                    return hit;
                }
            }

            Thread.Sleep(25);
        }

        return null;
    }

    [TestMethod]
    public void AChangeSavedByAnotherStoreReachesARunningTrayAsAChangedEvent()
    {
        using var temp = new TempFolder();
        string file = temp.File("settings.json");
        var tray = new JsonSettingsStore(file, new CapturingLog());
        var seen = new List<EarshotSettings>();
        tray.Changed += (_, s) =>
        {
            lock (seen)
            {
                seen.Add(s);
            }
        };
        using var watcher = new SettingsFileWatcher(tray, new CapturingLog(), static a => a(), Settle);
        Assert.AreEqual(20, tray.Current.Widget.LowBatteryThresholdPercent);

        // The command, in its own store, as a second process would be.
        var command = new JsonSettingsStore(file, new CapturingLog());
        var writer = new StringWriter();
        Assert.AreEqual(0, SettingsCommand.Run(["set", "low-battery-percent", "60"], command, writer), writer.ToString());

        EarshotSettings? got = WaitForChange(seen, s => s.Widget.LowBatteryThresholdPercent == 60);
        Assert.IsNotNull(got, "The tray's store never reported the change.");
        Assert.AreEqual(60, tray.Current.Widget.LowBatteryThresholdPercent);
    }

    [TestMethod]
    public void ATraysOwnSaveIsNotReadBackAsAChange()
    {
        using var temp = new TempFolder();
        var tray = new JsonSettingsStore(temp.File("settings.json"), new CapturingLog());
        int raised = 0;
        tray.Changed += (_, _) => Interlocked.Increment(ref raised);
        using var watcher = new SettingsFileWatcher(tray, new CapturingLog(), static a => a(), Settle);

        tray.Update(s => s.CheckForUpdatesAutomatically = true);
        Thread.Sleep(1000);

        Assert.AreEqual(1, Volatile.Read(ref raised), "Update raises Changed once; the watcher must not raise it again for the same bytes.");
    }

    [TestMethod]
    public void AnUnusableHandEditKeepsWhatTheTrayHasAndLeavesTheFileAloneUntilItIsFixed()
    {
        using var temp = new TempFolder();
        string file = temp.File("settings.json");
        var tray = new JsonSettingsStore(file, new CapturingLog());
        var seen = new List<EarshotSettings>();
        tray.Changed += (_, s) =>
        {
            lock (seen)
            {
                seen.Add(s);
            }
        };
        using var watcher = new SettingsFileWatcher(tray, new CapturingLog(), static a => a(), Settle);
        tray.Update(s => s.Widget = s.Widget with { LowBatteryThresholdPercent = 70 });
        lock (seen)
        {
            seen.Clear();
        }

        File.WriteAllText(file, "{ not json");
        Thread.Sleep(1000);

        Assert.AreEqual(70, tray.Current.Widget.LowBatteryThresholdPercent, "A file that cannot be read must not reset the tray.");
        Assert.AreEqual("{ not json", File.ReadAllText(file), "The person's half-done edit must not be moved or replaced.");
        Assert.IsEmpty(Directory.GetFiles(temp.Path, "settings.corrupt-*"), "Nothing is quarantined by a watcher.");

        var fixedFile = new JsonSettingsStore(temp.File("other.json"), new CapturingLog());
        fixedFile.Update(s => s.Widget = s.Widget with { LowBatteryThresholdPercent = 80 });
        File.Copy(temp.File("other.json"), file, overwrite: true);

        Assert.IsNotNull(WaitForChange(seen, s => s.Widget.LowBatteryThresholdPercent == 80), "The fixed file is taken up.");
    }

    // A stand-in for the tray's UI thread: one thread that runs what is posted to it, in order.
    private sealed class OneThread : IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;

        public OneThread()
        {
            _thread = new Thread(() =>
            {
                foreach (Action action in _queue.GetConsumingEnumerable())
                {
                    action();
                }
            })
            { IsBackground = true, Name = "test ui" };
            _thread.Start();
        }

        public int ThreadId => _thread.ManagedThreadId;

        public void Post(Action action) => _queue.Add(action);

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public void AChangeFromAnotherProgramIsRaisedOnTheContextTheWatcherWasGiven()
    {
        using var temp = new TempFolder();
        string file = temp.File("settings.json");
        var tray = new JsonSettingsStore(file, new CapturingLog());
        using var ui = new OneThread();
        var threads = new List<int>();
        var seen = new List<EarshotSettings>();
        tray.Changed += (_, s) =>
        {
            lock (seen)
            {
                threads.Add(Environment.CurrentManagedThreadId);
                seen.Add(s);
            }
        };
        using var watcher = new SettingsFileWatcher(tray, new CapturingLog(), ui.Post, Settle);

        var command = new JsonSettingsStore(file, new CapturingLog());
        command.Update(s => s.Widget = s.Widget with { LowBatteryThresholdPercent = 60 });

        Assert.IsNotNull(WaitForChange(seen, s => s.Widget.LowBatteryThresholdPercent == 60), "The tray's store never reported the change.");
        lock (seen)
        {
            CollectionAssert.AreEqual(new[] { ui.ThreadId }, threads.Distinct().ToArray(), "Changed must arrive on the thread the watcher was given, not a pool thread.");
        }
    }

    [TestMethod]
    public void AnEditThatIsStillHeldOpenByItsWriterIsTakenUpWhenTheWriterLetsGo()
    {
        using var temp = new TempFolder();
        string file = temp.File("settings.json");
        var log = new CapturingLog();
        var tray = new JsonSettingsStore(file, log);
        var seen = new List<EarshotSettings>();
        tray.Changed += (_, s) =>
        {
            lock (seen)
            {
                seen.Add(s);
            }
        };
        var other = new JsonSettingsStore(temp.File("other.json"), new CapturingLog());
        other.Update(s => s.Widget = s.Widget with { LowBatteryThresholdPercent = 80 });
        byte[] edit = File.ReadAllBytes(temp.File("other.json"));
        using var watcher = new SettingsFileWatcher(tray, log, static a => a(), Settle);

        // The writer has the file open for longer than the settle time, so the first read after the settle is refused.
        using (var held = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            held.SetLength(0);
            held.Write(edit);
            held.Flush();
            Thread.Sleep(Settle * 3);
        }

        Assert.IsNotNull(WaitForChange(seen, s => s.Widget.LowBatteryThresholdPercent == 80), "The edit was dropped after the file could not be read once.");
        Assert.IsTrue(log.Has(LogLevel.Warn, "not read for a reload"), "A read that failed after the settle must be logged at Warning.");
    }
}
