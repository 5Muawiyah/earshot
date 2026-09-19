using Earshot.Streaming;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace Earshot.Tests.Streaming;

// The only tests in this suite that call WinRT, and they only read: the selector string Windows supplies, and the
// device store's own list of paired devices that match it. Nothing is created, enabled, started or opened, no
// connection object ever exists, nothing is paired and nothing is sent over the radio. Each test fails if
// WindowsStreamingPlatform were not really talking to Windows, and says inconclusive, with the reason, where this
// machine cannot tell the difference.
[TestClass]
public sealed class WindowsStreamingPlatformBindingTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    // A fabricated CheckSupport that always said Supported, or always said too old, fails on a machine where the
    // independent answer disagrees.
    [TestMethod]
    public void SupportAgreesWithWindowsItself()
    {
        var platform = new WindowsStreamingPlatform(new CapturingLog());

        StreamingSupportCheck check = platform.CheckSupport();

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            Assert.AreEqual(StreamingSupport.BuildTooOld, check.Support);
            return;
        }

        string selector;
        try
        {
            selector = AudioPlaybackConnection.GetDeviceSelector();
        }
        catch (Exception ex)
        {
            Assert.AreEqual(StreamingSupport.TypeMissing, check.Support, "Windows would not answer here (" + ex.GetType().Name + "), and the platform must say the same.");
            return;
        }

        Assert.AreEqual(StreamingSupport.Supported, check.Support);
        Assert.IsTrue(check.Step.Ok);
        Assert.IsFalse(string.IsNullOrWhiteSpace(selector));

        // What keeps reading the list harmless: the selector is a query over device interfaces already in the device
        // store, not over association endpoints, which is the kind of query that scans for devices.
        Assert.IsFalse(selector.Contains("System.Devices.Aep", StringComparison.OrdinalIgnoreCase),
            "The selector Windows supplies has become an association endpoint query. Reading the list may now scan; look again at when Earshot reads it.");
    }

    // A fabricated list (always empty, or hardcoded) fails wherever this machine has a paired device that can send
    // audio. Where it has none, an honest empty list and a fabricated one look the same, so the test says so.
    [TestMethod]
    public async Task TheListIsTheOneWindowsGives()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            Assert.Inconclusive("Windows older than 10.0.19041 has no AudioPlaybackConnection to ask.");
            return;
        }

        string[] independent;
        try
        {
            DeviceInformationCollection found = await DeviceInformation.FindAllAsync(AudioPlaybackConnection.GetDeviceSelector()).AsTask().WaitAsync(Guard);

            // A loop, not a lambda: the platform analyser follows the guard above through straight code only.
            var ids = new List<string>();
            foreach (DeviceInformation information in found)
            {
                ids.Add(information.Id);
            }

            independent = ids.Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception ex)
        {
            Assert.Inconclusive("This machine could not read the device list independently (" + ex.GetType().Name + ", 0x" +
                ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + "), so there is nothing to compare with.");
            return;
        }

        var platform = new WindowsStreamingPlatform(new CapturingLog());
        StreamingDiscovery discovery = await platform.ListStreamCapableDevicesAsync(CancellationToken.None).WaitAsync(Guard);

        Assert.AreEqual(StreamingDiscoveryStatus.Ok, discovery.Status, StreamingLog.Describe(discovery.Step));
        CollectionAssert.AreEqual(independent, discovery.Devices.Select(d => d.DeviceId).Order(StringComparer.Ordinal).ToArray(),
            "The platform must report exactly the devices Windows itself lists for the selector.");
        Assert.AreEqual(0, platform.HeldConnections, "Reading the list creates no connection.");

        if (independent.Length == 0)
        {
            Assert.Inconclusive("No paired device on this machine can send audio to it, so an honest empty list and a made-up one look the same here.");
            return;
        }

        foreach (StreamingDevice device in discovery.Devices)
        {
            Assert.AreNotEqual(Guid.Empty, device.ContainerId, "Windows was asked for " + WindowsStreamingPlatform.ContainerIdProperty + " and the platform must carry it.");
        }
    }

    // Letting go of something that was never enabled is harmless, asks Windows nothing and never throws.
    [TestMethod]
    public void ReleasingWhatWasNeverEnabledDoesNothing()
    {
        var platform = new WindowsStreamingPlatform(new CapturingLog());

        StreamingReleaseOutcome outcome = platform.Release("never-enabled");

        Assert.IsFalse(outcome.Released);
        Assert.AreEqual(0, platform.HeldConnections);
    }
}
