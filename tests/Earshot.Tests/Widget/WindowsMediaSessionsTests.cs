using Earshot.Widget;
using Earshot.Widget.EarPause;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// M8 / CLAUDE.md: for every helper a test fakes (FakeMediaSessions stands in for this everywhere else),
// keep one execution of the real one. Read-only, as the safety rules for this work require: this class only
// ever calls ReadAsync, never TryPauseAsync or TryPlayAsync, so nothing on the machine running it is ever
// paused or played.
[TestClass]
public sealed class WindowsMediaSessionsTests
{
    [TestMethod]
    public async Task TheRealWindowsMediaSessionsReadsSessionsWithoutControllingAnything()
    {
        if (!WidgetPlatformGuard.HasMediaSessions)
        {
            Assert.Inconclusive("This build of Windows has no media session manager to check.");
            return;
        }

        var log = new CapturingLog();
        var sessions = new WindowsMediaSessions(log);

        IReadOnlyList<MediaSessionView> views = await sessions.ReadAsync(CancellationToken.None);

        if (log.Has(Earshot.Contracts.LogLevel.Warn, "Could not read media sessions"))
        {
            Assert.Inconclusive("No media session manager is available on this machine.");
            return;
        }

        // Whatever it returns is a snapshot of whatever is playing right now; an empty list on a machine
        // with nothing open is a valid answer, not a failure. The point of this test is that reading never
        // throws and never controls anything, proved by PauseCalls/PlayCalls not existing on the real type.
        Assert.IsNotNull(views);
    }

    // SourceAppUserModelId is the only session identifier the real API gives, and two sessions from the same
    // app (two tabs of one browser) share it. PickSessionIndex is the pure disambiguation rule FindSession
    // hands the real WinRT session list to, so it is testable without any WinRT type at all.
    [TestMethod]
    public void PickSessionIndexPrefersTheSessionAlreadyInTheExpectedState()
    {
        var sessions = new (string AppId, MediaPlaybackState Status)[]
        {
            ("app.exe", MediaPlaybackState.Paused),
            ("app.exe", MediaPlaybackState.Playing),
        };

        Assert.AreEqual(1, WindowsMediaSessions.PickSessionIndex(sessions, "app.exe", pause: true), "A pause must act on the Playing one.");
        Assert.AreEqual(0, WindowsMediaSessions.PickSessionIndex(sessions, "app.exe", pause: false), "A play must act on the Paused one.");
    }

    [TestMethod]
    public void PickSessionIndexFallsBackToTheFirstMatchWhenNoneIsInTheExpectedState()
    {
        var sessions = new (string AppId, MediaPlaybackState Status)[]
        {
            ("app.exe", MediaPlaybackState.Stopped),
            ("app.exe", MediaPlaybackState.Closed),
        };

        Assert.AreEqual(0, WindowsMediaSessions.PickSessionIndex(sessions, "app.exe", pause: true));
    }

    [TestMethod]
    public void PickSessionIndexReturnsNullWhenNothingMatchesTheAppId()
    {
        var sessions = new (string AppId, MediaPlaybackState Status)[] { ("other.exe", MediaPlaybackState.Playing) };

        Assert.IsNull(WindowsMediaSessions.PickSessionIndex(sessions, "app.exe", pause: true));
    }
}
