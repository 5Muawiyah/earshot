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
//
// Each real WinRtAdvertisementSource constructed here calls WidgetRealSurfaceGuardTests.AllowRealConstruction(RealWidgetSurface.AdvertisementSource),
// so the assembly-wide guard can tell this named real execution apart from an unnoticed one elsewhere: every
// TrayContext test now gets a fake advertisement source instead (TrayContextTests.TrayHarness).
[TestClass]
public sealed class WinRtAdvertisementSourceBindingTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    [TestMethod]
    [SupportedOSPlatform("windows10.0.19041.0")]
    public async Task TheWatcherIsPassiveAndStartsOrSaysWhy()
    {
        using var source = new WinRtAdvertisementSource();
        WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.AdvertisementSource);
        var stoppedTcs = new TaskCompletionSource<AdvertisementSourceStopped>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedTcs = new TaskCompletionSource<AdvertisementSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Stopped += (sender, e) => stoppedTcs.TrySetResult(e);
        source.Received += (sender, e) => receivedTcs.TrySetResult(e);

        StepOutcome startStep = source.Start(1);

        if (!await WaitForStartedAsync(source))
        {
            // The documented outcome with the Bluetooth radio switched off: Start itself throws a COMException
            // (HRESULT 0x800710DF, ERROR_DEVICE_NOT_AVAILABLE) rather than raising Stopped with
            // RadioNotAvailable. The literal is written here, not read from the product's constant, so the
            // raw code is proved rather than compared with itself.
            if (startStep.Code == unchecked((int)0x800710DF))
            {
                AssertBluetoothOffStart(startStep, source);
                return;
            }

            AdvertisementSourceStopped? stopped = stoppedTcs.Task.IsCompleted ? stoppedTcs.Task.Result : null;

            // Inconclusive only when the reason is that no radio (or no watcher support at all on this build)
            // is present; anything else, including a Start that failed synchronously with no Stopped event at
            // all (access denied, for instance), is a failure, not something to wave through as "could not
            // tell on this machine".
            if (stopped is not null && stopped.ErrorName != "RadioNotAvailable")
            {
                Assert.Fail("The watcher failed to start with an error other than RadioNotAvailable: " + stopped.ErrorName + ".");
            }

            if (stopped is null && !startStep.Ok && startStep.Code != Earshot.Contracts.NativeCodes.NotAvailable)
            {
                Assert.Fail("Start failed with no Stopped event and a reason other than no watcher support: " +
                    startStep.CodeName + (startStep.Detail is null ? "." : " (" + startStep.Detail + ")."));
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
        WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.AdvertisementSource);
        var stoppedTcs = new TaskCompletionSource<AdvertisementSourceStopped>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Stopped += (sender, e) => stoppedTcs.TrySetResult(e);

        StepOutcome startStep = source.Start(1);
        bool started = await WaitForStartedAsync(source);
        if (!started)
        {
            // Bluetooth off: Start reported the documented code, the source is not running, and Stop on it
            // still answers with a recorded step (it must not throw or leave the source half-stopped).
            if (startStep.Code == unchecked((int)0x800710DF))
            {
                AssertBluetoothOffStart(startStep, source);
                StepOutcome stopStep = source.Stop();
                Assert.AreEqual("watcher-stop", stopStep.Step, "Stop after a refused Start must still be a recorded step.");
                Assert.AreNotEqual(AdvertisementSourceState.Started, source.State);
                return;
            }

            // Inconclusive only for genuinely no watcher support on this build; anything else is a real
            // failure Stop cannot be meaningfully exercised against either, but for its own, different reason.
            if (!startStep.Ok && startStep.Code != Earshot.Contracts.NativeCodes.NotAvailable)
            {
                Assert.Fail("Start failed with a reason other than no watcher support: " + startStep.CodeName +
                    (startStep.Detail is null ? "." : " (" + startStep.Detail + ")."));
            }

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

    // Watcher lifecycle: a delayed retry that was already past its early checks when Close ran must not
    // resurrect a native watcher behind this wrapper's back once its own, blocking Start() call finally
    // returns. Proved against the real wrapper: Dispose, then Start, and the refusal must be a recorded
    // step, never a silent no-op and never an actual native BluetoothLEAdvertisementWatcher.
    [TestMethod]
    public void StartAfterDisposeRefuses()
    {
        var source = new WinRtAdvertisementSource();
        WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.AdvertisementSource);
        source.Dispose();

        StepOutcome step = source.Start(1);

        Assert.IsFalse(step.Ok, "Start after Dispose must refuse rather than resurrect a native watcher.");
        Assert.AreEqual(Earshot.Contracts.NativeCodes.NotAttempted, step.Code,
            "Start after Dispose must record a refusal, not attempt a native call.");
    }

    private static void AssertBluetoothOffStart(StepOutcome startStep, WinRtAdvertisementSource source)
    {
        Assert.IsFalse(startStep.Ok, "A Start that threw because Bluetooth is off is not a success.");
        Assert.AreEqual(unchecked((int)0x800710DF), startStep.Code, "The raw code must be kept.");
        Assert.AreEqual("ERROR_DEVICE_NOT_AVAILABLE", startStep.CodeName);
        Assert.IsNotNull(startStep.Detail);
        StringAssert.Contains(startStep.Detail, "Bluetooth is off");
        StringAssert.Contains(startStep.Detail, "0x800710DF", "The raw code must be in the detail as well, for the log.");
        Assert.AreNotEqual(AdvertisementSourceState.Started, source.State, "A source whose Start threw must not read as running.");
        Assert.IsTrue(AdvertisementSourceCodes.IsRadioOff(startStep));
    }

    // The service shows a Start that threw with Bluetooth off exactly as it shows a Stopped event carrying
    // RadioNotAvailable, so the two constants it uses must be the real enum's own value and name.
    [TestMethod]
    [SupportedOSPlatform("windows10.0.19041.0")]
    public void TheRadioNotAvailableConstantsAreTheEnumsOwnValueAndName()
    {
        // Through a helper, so the analyser does not fold two compile-time constants into an always-true check.
        AssertSame((int)Windows.Devices.Bluetooth.BluetoothError.RadioNotAvailable, AdvertisementSourceCodes.RadioNotAvailableCode);
        AssertSame(Windows.Devices.Bluetooth.BluetoothError.RadioNotAvailable.ToString(), AdvertisementSourceCodes.RadioNotAvailableName);
    }

    private static void AssertSame<T>(T expected, T actual) => Assert.AreEqual(expected, actual);

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
