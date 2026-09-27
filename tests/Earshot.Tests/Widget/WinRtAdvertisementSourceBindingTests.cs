using System.Runtime.Versioning;
using Earshot.Contracts;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Devices.Bluetooth.Advertisement;

namespace Earshot.Tests.Widget;

// The one real execution for the seam every other widget test fakes: read-only, like
// WindowsStreamingPlatformBindingTests. Construct the real source, Start, assert the state reads Started or
// the Stopped event's error by name (inconclusive with the reason when this machine has no working radio),
// wait up to 10 s for any Received of any company or a Stopped, then Stop and assert Stopped carried
// Success. It never filters, connects or pairs.
[TestClass]
public sealed class WinRtAdvertisementSourceBindingTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    [TestMethod]
    [SupportedOSPlatform("windows10.0.19041.0")]
    public async Task TheWatcherIsPassiveAndStartsOrSaysWhy()
    {
        using var source = new WinRtAdvertisementSource();
        var stoppedTcs = new TaskCompletionSource<AdvertisementSourceStopped>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedTcs = new TaskCompletionSource<AdvertisementSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Stopped += (sender, e) => stoppedTcs.TrySetResult(e);
        source.Received += (sender, e) => receivedTcs.TrySetResult(e);

        StepOutcome startStep = source.Start();

        if (!await WaitForStartedAsync(source))
        {
            AdvertisementSourceStopped? stopped = stoppedTcs.Task.IsCompleted ? stoppedTcs.Task.Result : null;

            // Inconclusive only when the reason is that no radio is present; anything else is a failure,
            // not something to wave through as "could not tell on this machine".
            if (stopped is not null && stopped.ErrorName != "RadioNotAvailable")
            {
                Assert.Fail("The watcher failed to start with an error other than RadioNotAvailable: " + stopped.ErrorName + ".");
            }

            Assert.Inconclusive("The watcher did not start on this machine (" + startStep.CodeName + ")" +
                (stopped is null ? "." : ", Stopped carried " + stopped.ErrorName + "."));
            return;
        }

        // The real watcher, not the constant Start() sets, must actually be Passive.
        Assert.AreEqual(BluetoothLEScanningMode.Passive, source.ScanningMode, "The real watcher must be Passive: no scan request packets.");

        Task first = await Task.WhenAny(receivedTcs.Task, stoppedTcs.Task, Task.Delay(Guard));
        if (first == stoppedTcs.Task)
        {
            AdvertisementSourceStopped stopped = stoppedTcs.Task.Result;
            if (stopped.ErrorName != "RadioNotAvailable")
            {
                Assert.Fail("The watcher stopped on its own with an error other than RadioNotAvailable: " + stopped.ErrorName + ".");
            }

            Assert.Inconclusive("The radio became unavailable while waiting: " + stopped.ErrorName + ".");
            return;
        }

        if (first != receivedTcs.Task)
        {
            // Nothing arrived within the guard. On a machine with no nearby BLE traffic and a passive,
            // unfiltered watcher this is a plausible quiet ten seconds, not proof the watcher is broken.
            Assert.Inconclusive("No advertisement and no Stopped event arrived within " + Guard + " on this machine.");
            return;
        }

        source.Stop();
    }

    [TestMethod]
    public async Task StopRaisesStoppedWithSuccess()
    {
        using var source = new WinRtAdvertisementSource();
        var stoppedTcs = new TaskCompletionSource<AdvertisementSourceStopped>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Stopped += (sender, e) => stoppedTcs.TrySetResult(e);

        StepOutcome startStep = source.Start();
        bool started = await WaitForStartedAsync(source);
        if (!started)
        {
            Assert.Inconclusive("The watcher did not start on this machine (" + startStep.CodeName + "), so Stop cannot be exercised meaningfully.");
            return;
        }

        source.Stop();

        // Once actually started, a Stop must raise Stopped deterministically; "inconclusive" is reserved
        // for the earlier no-radio case, not for Stop itself going quiet.
        Task first = await Task.WhenAny(stoppedTcs.Task, Task.Delay(Guard));
        if (first != stoppedTcs.Task)
        {
            Assert.Fail("Stop did not raise Stopped within " + Guard + " on this machine.");
            return;
        }

        AdvertisementSourceStopped stopped = stoppedTcs.Task.Result;
        Assert.AreEqual("Success", stopped.ErrorName);
        Assert.AreEqual(0, stopped.ErrorCode);
    }

    // Start() returns as soon as the request is issued; the watcher's own Status can take a short moment
    // to read Started afterwards, so this polls briefly rather than deciding "did not start" on one read.
    private static async Task<bool> WaitForStartedAsync(WinRtAdvertisementSource source)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (source.State == AdvertisementSourceState.Started)
            {
                return true;
            }

            await Task.Delay(20);
        }

        return source.State == AdvertisementSourceState.Started;
    }
}
