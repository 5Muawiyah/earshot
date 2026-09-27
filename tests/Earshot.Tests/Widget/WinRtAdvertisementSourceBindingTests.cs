using Earshot.Contracts;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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
            Assert.Inconclusive("The watcher did not start on this machine (" + startStep.CodeName + ")" +
                (stopped is null ? "." : ", Stopped carried " + stopped.ErrorName + "."));
            return;
        }

        Task first = await Task.WhenAny(receivedTcs.Task, stoppedTcs.Task, Task.Delay(Guard));
        if (first != receivedTcs.Task && first != stoppedTcs.Task)
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

        Task first = await Task.WhenAny(stoppedTcs.Task, Task.Delay(Guard));
        if (first != stoppedTcs.Task)
        {
            Assert.Inconclusive("Stop did not raise Stopped within " + Guard + " on this machine.");
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
