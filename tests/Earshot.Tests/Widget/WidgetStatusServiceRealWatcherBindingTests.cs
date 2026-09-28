using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Tests.Phase3;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The one real execution proving a late Stopped(Success) from a deliberate Stop is told apart from a
// genuine one: the service, wired to the real WinRtAdvertisementSource, doing Suspend then an immediate
// Resume - the same Stop-then-Start shape
// WinRtAdvertisementSourceBindingTests exercises on the source alone - and checked afterwards for the false
// "Stopped" the old, ungenerationed code showed. Read-only, like that class: it never connects, pairs or
// touches a device node. Inconclusive only when this machine never got the watcher to Started at all.
[TestClass]
public sealed class WidgetStatusServiceRealWatcherBindingTests
{
    private static readonly TimeSpan StartGuard = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LateEventGuard = TimeSpan.FromSeconds(2);

    [TestMethod]
    public async Task SuspendThenAnImmediateResumeDoesNotLeaveTheServiceShowingAFalseStopped()
    {
        using var temp = new TempFolder();
        var log = new CapturingLog();
        var settings = new JsonSettingsStore(temp.File("settings.json"), log);
        var deviceMonitor = new FakeDeviceMonitor(TimeProvider.System);
        var claimStore = new ClaimStore(temp.File("claim.json"), log);
        // A named real execution: each real source the service builds is declared to the assembly-wide
        // guard as it is built (a passive watcher, no window, nothing sent to any device).
        using var service = new WidgetStatusService(
            () =>
            {
                WidgetRealSurfaceGuardTests.AllowRealConstruction();
                return new WinRtAdvertisementSource();
            },
            claimStore, settings, deviceMonitor, () => null, log,
            action => action(), TimeProvider.System);

        service.Start();

        if (!await WaitUntilAsync(() => service.Current.Watcher == WidgetWatcherState.Started, StartGuard))
        {
            WidgetSnapshot snapshot = service.Current;
            if (snapshot.Watcher == WidgetWatcherState.Stopped && snapshot.WatcherErrorName != "RadioNotAvailable")
            {
                Assert.Fail("The watcher failed to start with an error other than RadioNotAvailable: " + snapshot.WatcherErrorName + ".");
            }

            Assert.Inconclusive("The watcher did not start on this machine within " + StartGuard + ".");
            return;
        }

        // Suspend's own Stop() call, then an immediate Resume: on the real watcher this is exactly the shape
        // that produced a late Stopped(Success) about 1 ms after Start returned.
        service.Suspend();
        service.Resume();

        // Give any late Stopped from the suspended run's own Stop a real chance to arrive and be misread as
        // the current state before checking: the bug this proves fixed is a race, not a guarantee, so the
        // guard is a wait for the wrong state to appear, not a sleep-then-assert-immediately.
        bool wrongStateAppeared = await WaitUntilAsync(() => service.Current.Watcher != WidgetWatcherState.Started, LateEventGuard);

        Assert.IsFalse(
            wrongStateAppeared,
            "A late Stopped from the suspended run must not show as the service's current watcher state: it read " +
            service.Current.Watcher + " (" + service.Current.WatcherErrorName + ").");
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }
}
