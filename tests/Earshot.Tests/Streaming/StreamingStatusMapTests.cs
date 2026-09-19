using Earshot.Streaming;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Media.Audio;

namespace Earshot.Tests.Streaming;

// Acceptance test 4. The mapping is pinned against the real WinRT enums, not against a copy of them, which is why
// this file names two Windows.* types. They are values in a managed projection assembly: nothing here calls into
// Windows, creates a connection or reads a device. The analyser still wants the platform guard, and gets it.
[TestClass]
public sealed class StreamingStatusMapTests
{
    [TestMethod]
    public void OpenStatusMapIsExhaustiveAndCorrect()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            Assert.Inconclusive("Windows older than 10.0.19041: the platform analyser does not allow these types to be named here.");
            return;
        }

        Assert.AreEqual(StreamingOpenStatus.Open, StreamingStatusMap.FromWinRt(AudioPlaybackConnectionOpenResultStatus.Success));
        Assert.AreEqual(StreamingOpenStatus.TimedOut, StreamingStatusMap.FromWinRt(AudioPlaybackConnectionOpenResultStatus.RequestTimedOut));
        Assert.AreEqual(StreamingOpenStatus.DeniedBySystem, StreamingStatusMap.FromWinRt(AudioPlaybackConnectionOpenResultStatus.DeniedBySystem));
        Assert.AreEqual(StreamingOpenStatus.UnknownFailure, StreamingStatusMap.FromWinRt(AudioPlaybackConnectionOpenResultStatus.UnknownFailure));

        // A member a later Windows adds is an unknown failure, never an exception and never a success.
        Assert.AreEqual(StreamingOpenStatus.UnknownFailure, StreamingStatusMap.FromWinRt((AudioPlaybackConnectionOpenResultStatus)42));

        // The documented values, and no member this test does not know about.
        // https://learn.microsoft.com/en-us/uwp/api/windows.media.audio.audioplaybackconnectionopenresultstatus
        Assert.AreEqual(0, ValueOf<AudioPlaybackConnectionOpenResultStatus>("Success"));
        Assert.AreEqual(1, ValueOf<AudioPlaybackConnectionOpenResultStatus>("RequestTimedOut"));
        Assert.AreEqual(2, ValueOf<AudioPlaybackConnectionOpenResultStatus>("DeniedBySystem"));
        Assert.AreEqual(3, ValueOf<AudioPlaybackConnectionOpenResultStatus>("UnknownFailure"));
        Assert.AreEqual(4, Enum.GetValues<AudioPlaybackConnectionOpenResultStatus>().Length, "The projection has a member this map was not written for.");
    }

    [TestMethod]
    public void LinkStateMapIsExhaustiveAndCorrect()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            Assert.Inconclusive("Windows older than 10.0.19041: the platform analyser does not allow these types to be named here.");
            return;
        }

        Assert.AreEqual(StreamingLinkState.Closed, StreamingStatusMap.FromWinRt(AudioPlaybackConnectionState.Closed));
        Assert.AreEqual(StreamingLinkState.Opened, StreamingStatusMap.FromWinRt(AudioPlaybackConnectionState.Opened));
        Assert.AreEqual(StreamingLinkState.Unknown, StreamingStatusMap.FromWinRt((AudioPlaybackConnectionState)42));

        // Two members: Closed and Opened. There is no Connecting, Opening or Failed to map.
        // https://learn.microsoft.com/en-us/uwp/api/windows.media.audio.audioplaybackconnectionstate
        Assert.AreEqual(0, ValueOf<AudioPlaybackConnectionState>("Closed"));
        Assert.AreEqual(1, ValueOf<AudioPlaybackConnectionState>("Opened"));
        Assert.AreEqual(2, Enum.GetValues<AudioPlaybackConnectionState>().Length);
    }

    // Earshot's own enums start at a value that claims nothing.
    [TestMethod]
    public void EveryZeroValueSaysNothingHasHappenedYet()
    {
        Assert.AreEqual(0, ValueOf<StreamingSupport>("NotChecked"));
        Assert.AreEqual(0, ValueOf<StreamingDiscoveryStatus>("NotStarted"));
        Assert.AreEqual(0, ValueOf<StreamingEnableStatus>("NotStarted"));
        Assert.AreEqual(0, ValueOf<StreamingOpenStatus>("NotStarted"));
        Assert.AreEqual(0, ValueOf<StreamingLinkState>("Unknown"));
        Assert.AreEqual(0, ValueOf<StreamingMenuCommand>("None"));
    }

    // Read by name when the test runs, so the value compared is the one in the assembly that was loaded and not a
    // constant the compiler folded into this one.
    private static int ValueOf<T>(string name)
        where T : struct, Enum =>
        Convert.ToInt32(Enum.Parse<T>(name), System.Globalization.CultureInfo.InvariantCulture);
}
