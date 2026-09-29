using System.Diagnostics;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Tests.Phase1;
using Earshot.Tests.Phase3;
using Earshot.Tray;
using Earshot.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Update;

// "Check automatically": off by default, once a day at most, a check and never a download; and the settings and
// menu that carry it.
[TestClass]
public sealed class UpdateAutoCheckTests
{
    private sealed class MemoryStamp : IUpdateCheckStamp
    {
        public DateTimeOffset? Value { get; set; }

        public int Writes { get; private set; }

        public DateTimeOffset? Read() => Value;

        public void Write(DateTimeOffset when)
        {
            Value = when;
            Writes++;
        }
    }

    private sealed class Loop : IDisposable
    {
        private readonly CancellationTokenSource _cancel = new();
        private int _looks;
        private int _checks;

        public Loop(bool enabled = true, DateTimeOffset? last = null)
        {
            Enabled = enabled;
            Stamp.Value = last;
            var auto = new UpdateAutoCheck(Time, () =>
            {
                Interlocked.Increment(ref _looks);
                return Enabled;
            }, Stamp, _ =>
            {
                Interlocked.Increment(ref _checks);
                return Task.CompletedTask;
            }, TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(30), Log);
            Run = Task.Run(() => auto.RunAsync(_cancel.Token));
        }

        public ManualTimeProvider Time { get; } = new();

        public CapturingLog Log { get; } = new();

        public MemoryStamp Stamp { get; } = new();

        public bool Enabled { get; set; }

        public Task Run { get; }

        public int Looks => Volatile.Read(ref _looks);

        public int Checks => Volatile.Read(ref _checks);

        // Waits until the loop has armed its next wait, so an Advance is never made before it is sleeping.
        public void WaitUntilSleeping()
        {
            var watch = Stopwatch.StartNew();
            while (Time.ArmedTimers < 1)
            {
                Assert.IsLessThan(TimeSpan.FromSeconds(10), watch.Elapsed, "The loop never went back to sleep.");
                Thread.Sleep(1);
            }
        }

        // Moves the clock and waits until the loop has looked at the setting once more (or ended).
        public void AdvanceAndWait(TimeSpan by)
        {
            int before = Looks;
            Time.Advance(by);
            var watch = Stopwatch.StartNew();
            while (Looks == before)
            {
                Assert.IsLessThan(TimeSpan.FromSeconds(10), watch.Elapsed, "The loop did not wake.");
                Thread.Sleep(1);
            }

            WaitUntilSleeping();
        }

        public void Dispose()
        {
            _cancel.Cancel();
            Run.GetAwaiter().GetResult();
            _cancel.Dispose();
        }
    }

    // ----- the setting -----

    [TestMethod]
    public void CheckAutomaticallyIsOffByDefault()
    {
        Assert.IsFalse(new EarshotSettings().CheckForUpdatesAutomatically);
    }

    [TestMethod]
    public void ASettingsFileFromBeforeTheSettingExistedReadsAsOffAndNothingIsContacted()
    {
        using var temp = new TempFolder();
        File.WriteAllText(temp.File("settings.json"), "{ \"SchemaVersion\": 1, \"DeviceMatch\": \"AirPods\" }");

        var store = new JsonSettingsStore(temp.File("settings.json"), new CapturingLog());

        Assert.AreEqual(SettingsLoadStatus.Loaded, store.LastLoadStatus);
        Assert.IsFalse(store.Current.CheckForUpdatesAutomatically);
    }

    [TestMethod]
    public void TheSettingIsSavedAndReadBack()
    {
        using var temp = new TempFolder();
        var store = new JsonSettingsStore(temp.File("settings.json"), new CapturingLog());

        store.Update(s => s.CheckForUpdatesAutomatically = true);

        StringAssert.Contains(File.ReadAllText(temp.File("settings.json")), "\"CheckForUpdatesAutomatically\": true");
        Assert.IsTrue(new JsonSettingsStore(temp.File("settings.json"), new CapturingLog()).Current.CheckForUpdatesAutomatically);
    }

    // ----- the menu -----

    [TestMethod]
    public void TheMenuCarriesCheckForUpdatesAndTheUncheckedAutomaticItem()
    {
        MenuState state = MenuModel.Build(Phase1Fixtures.NoDevice(), null, null, new EarshotSettings(), busy: false, StartupState.Off);

        Assert.AreEqual("Check for updates", state.CheckForUpdates.Text);
        Assert.IsTrue(state.CheckForUpdates.Visible);
        Assert.IsTrue(state.CheckForUpdates.Enabled);
        Assert.AreEqual("Check automatically", state.CheckAutomatically.Text);
        Assert.IsFalse(state.CheckAutomatically.Checked, "Off until the owner turns it on.");
        Assert.IsTrue(state.CheckAutomatically.Visible);
    }

    [TestMethod]
    public void TheAutomaticItemShowsTheSetting()
    {
        var on = new EarshotSettings { CheckForUpdatesAutomatically = true };

        Assert.IsTrue(MenuModel.Build(Phase1Fixtures.NoDevice(), null, null, on, busy: false, StartupState.Off).CheckAutomatically.Checked);
    }

    [TestMethod]
    public void ACheckIsNotStoppedByAConnectInFlightButIsByAnotherUpdateStep()
    {
        MenuState busy = MenuModel.Build(Phase1Fixtures.NoDevice(), null, null, new EarshotSettings(), busy: true, StartupState.Off);
        MenuState updating = MenuModel.Build(Phase1Fixtures.NoDevice(), null, null, new EarshotSettings(), busy: false, StartupState.Off, updateInProgress: true);

        Assert.IsTrue(busy.CheckForUpdates.Enabled, "A check reads GitHub and touches no device.");
        Assert.IsFalse(busy.CheckAutomatically.Enabled, "A settings change waits for a device action, like the other toggles.");
        Assert.IsFalse(updating.CheckForUpdates.Enabled);
    }

    // ----- when a check is due -----

    [TestMethod]
    public void ACheckIsDueWhenNeverMadeAfterADayOrWhenTheClockWentBack()
    {
        var now = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

        Assert.IsTrue(UpdateAutoCheck.IsDue(null, now));
        Assert.IsFalse(UpdateAutoCheck.IsDue(now - TimeSpan.FromHours(23), now));
        Assert.IsFalse(UpdateAutoCheck.IsDue(now, now));
        Assert.IsTrue(UpdateAutoCheck.IsDue(now - TimeSpan.FromHours(24), now));
        Assert.IsTrue(UpdateAutoCheck.IsDue(now - TimeSpan.FromDays(9), now));
        Assert.IsFalse(UpdateAutoCheck.IsDue(now + TimeSpan.FromHours(2), now), "A little in the future is not proof the clock went back.");
        Assert.IsTrue(UpdateAutoCheck.IsDue(now + TimeSpan.FromDays(2), now), "A record days ahead cannot say when the last check was.");
    }

    [TestMethod]
    public void TheDailyCheckWaitsAMinuteAfterStartupAndLooksTwiceAnHour()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(60), UpdateAutoCheck.StartupDelay);
        Assert.AreEqual(TimeSpan.FromMinutes(30), UpdateAutoCheck.PollInterval);
        Assert.AreEqual(TimeSpan.FromDays(1), UpdateAutoCheck.Interval);
    }

    // ----- the loop -----

    [TestMethod]
    public void TheFirstCheckIsAfterStartupNotBefore()
    {
        using var loop = new Loop();
        loop.WaitUntilSleeping();

        loop.Time.Advance(TimeSpan.FromSeconds(59));
        Thread.Sleep(50);
        Assert.AreEqual(0, loop.Checks, "Nothing is checked in the first minute.");
        Assert.AreEqual(0, loop.Stamp.Writes);

        loop.AdvanceAndWait(TimeSpan.FromSeconds(1));

        Assert.AreEqual(1, loop.Checks);
        Assert.AreEqual(1, loop.Stamp.Writes, "The check is recorded when it starts.");
    }

    [TestMethod]
    public void ThereIsAtMostOneCheckADayHoweverOftenTheLoopLooks()
    {
        using var loop = new Loop();
        loop.WaitUntilSleeping();
        loop.AdvanceAndWait(TimeSpan.FromSeconds(60));
        Assert.AreEqual(1, loop.Checks);

        for (int i = 0; i < 47; i++)
        {
            loop.AdvanceAndWait(TimeSpan.FromMinutes(30));
        }

        Assert.AreEqual(1, loop.Checks, "Twenty-three and a half hours later there has still been one.");

        loop.AdvanceAndWait(TimeSpan.FromMinutes(30));

        Assert.AreEqual(2, loop.Checks, "A day after the first, the second.");
        Assert.AreEqual(2, loop.Stamp.Writes);
    }

    [TestMethod]
    public void NothingIsCheckedWhileTheSettingIsOffAndTheNextLookAfterItIsOnChecks()
    {
        using var loop = new Loop(enabled: false);
        loop.WaitUntilSleeping();
        loop.AdvanceAndWait(TimeSpan.FromSeconds(60));
        loop.AdvanceAndWait(TimeSpan.FromDays(3));

        Assert.AreEqual(0, loop.Checks, "Off: not even after days.");
        Assert.AreEqual(0, loop.Stamp.Writes);
        Assert.IsNull(loop.Stamp.Value);

        loop.Enabled = true;
        loop.AdvanceAndWait(TimeSpan.FromMinutes(30));

        Assert.AreEqual(1, loop.Checks);
    }

    [TestMethod]
    public void ARestartWithinTheDayDoesNotCheckAgainButOneAfterTheDayDoes()
    {
        DateTimeOffset start = new ManualTimeProvider().GetUtcNow();

        using (var recent = new Loop(last: start - TimeSpan.FromHours(2)))
        {
            recent.WaitUntilSleeping();
            recent.AdvanceAndWait(TimeSpan.FromSeconds(60));
            Assert.AreEqual(0, recent.Checks, "Checked two hours ago, so not now.");
        }

        using var stale = new Loop(last: start - TimeSpan.FromHours(25));
        stale.WaitUntilSleeping();
        stale.AdvanceAndWait(TimeSpan.FromSeconds(60));
        Assert.AreEqual(1, stale.Checks);
    }

    [TestMethod]
    public void TheLoopEndsWhenTheProgramCloses()
    {
        var loop = new Loop();
        loop.WaitUntilSleeping();

        loop.Dispose();

        Assert.IsTrue(loop.Run.IsCompletedSuccessfully, "Cancelling ends the loop quietly.");
    }

    private sealed class ThrowingStamp : IUpdateCheckStamp
    {
        public DateTimeOffset? Read() => throw new InvalidOperationException("the stamp could not be read");

        public void Write(DateTimeOffset when)
        {
        }
    }

    // The loop runs on a pool thread nobody awaits. Something it did not expect ends it, and that is said in the log
    // with the exception, instead of the daily checks stopping in silence.
    [TestMethod]
    public void AnExceptionThatEndsTheLoopIsLoggedWithItsType()
    {
        var time = new ManualTimeProvider();
        var log = new CapturingLog();
        var auto = new UpdateAutoCheck(time, () => true, new ThrowingStamp(), _ => Task.CompletedTask, TimeSpan.Zero, TimeSpan.FromMinutes(30), log);

        Task run = Task.Run(() => auto.RunAsync(CancellationToken.None));
        Assert.IsTrue(run.Wait(TimeSpan.FromSeconds(10)), "The loop did not end.");

        Assert.IsTrue(log.Has(LogLevel.Error, "InvalidOperationException"), "The loop ended without saying why.");
        Assert.IsTrue(log.Has(LogLevel.Error, "no more automatic checks"));
    }

    // ----- the stamp file -----

    [TestMethod]
    public void TheStampFileRoundTripsATimeAndCreatesItsFolder()
    {
        using var temp = new TempFolder();
        var stamp = new FileUpdateCheckStamp(temp.File("update\\last-check.txt"), new CapturingLog());
        var when = new DateTimeOffset(2026, 9, 29, 12, 34, 56, TimeSpan.Zero);

        Assert.IsNull(stamp.Read(), "Never checked.");
        stamp.Write(when);

        Assert.AreEqual(when, stamp.Read());
    }

    [TestMethod]
    public void AStampThatIsNotATimeCountsAsNeverCheckedAndIsLogged()
    {
        using var temp = new TempFolder();
        File.WriteAllText(temp.File("last-check.txt"), "yesterday-ish");
        var log = new CapturingLog();

        Assert.IsNull(new FileUpdateCheckStamp(temp.File("last-check.txt"), log).Read());
        Assert.IsTrue(log.Has(LogLevel.Warn, "does not hold a time"));
    }

    [TestMethod]
    public void AStampThatCannotBeWrittenIsLoggedNotThrown()
    {
        using var temp = new TempFolder();
        File.WriteAllText(temp.File("blocker"), "a file where a folder is needed");
        var log = new CapturingLog();

        new FileUpdateCheckStamp(temp.File("blocker\\last-check.txt"), log).Write(DateTimeOffset.UtcNow);

        Assert.IsTrue(log.Has(LogLevel.Warn, "could not be written"));
    }
}
