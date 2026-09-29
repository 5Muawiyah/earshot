using Earshot.App;
using Earshot.Audio;
using Earshot.Contracts;
using Earshot.Tests.Integration.Coordinator;
using Earshot.Widget.EarPause;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase2;

// For every helper a test fakes (FakeRenderActivity stands in for this everywhere else), one execution of the real one:
// the real audio worker and the real Core Audio session interfaces, read-only. This only lists endpoints and asks each
// audio session for its state; nothing is started, stopped, paused or changed, so it is safe on any machine, and it
// is run on the private desktop like every test that could show a window (it shows none).
[TestClass]
public sealed class CoreAudioRenderActivityRealTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public TestContext TestContext { get; set; } = null!;

    // A container no endpoint has: the real enumeration runs, finds no render endpoint of it, and says silent, or says
    // unknown with the code of the step that failed. Never a guess in either direction.
    [TestMethod]
    public async Task TheRealReaderOnAContainerNoEndpointHasIsSilentOrUnknownWithAReason()
    {
        var log = new CapturingLog();
        await using var worker = new AudioWorker(log);
        var activity = new CoreAudioRenderActivity(worker);

        RenderActivityReading reading = await activity.ReadAsync(Guid.NewGuid(), CancellationToken.None).WaitAsync(Timeout);
        TestContext.WriteLine("unknown container: state " + reading.State + ", steps " + reading.Steps.Count);

        if (reading.State == RenderActivityState.Unknown)
        {
            Assert.IsTrue(reading.Steps.Any(s => !s.Ok), "The read was unknown and named no step that failed.");
        }
        else
        {
            Assert.AreEqual(RenderActivityState.Silent, reading.State, "Nothing can be playing to a device that is not there.");
            Assert.AreEqual(0, reading.ActiveSessions);
        }

        Assert.IsFalse(log.Entries.Any(e => e.Level == LogLevel.Error), string.Join(Environment.NewLine, log.Entries.Select(e => e.Message)));
    }

    // The path that opens a real endpoint's session manager and reads every session's state: the default render
    // endpoint's container, so it runs wherever there is an audio device at all.
    [TestMethod]
    public async Task TheRealReaderReadsTheSessionsOfTheDefaultRenderEndpointWithoutAFailedStep()
    {
        var log = new CapturingLog();
        await using var worker = new AudioWorker(log);
        Guid container = await DefaultRenderContainerAsync(worker);
        if (container == Guid.Empty)
        {
            Assert.Inconclusive("This machine has no default render endpoint to read the audio sessions of.");
            return;
        }

        var activity = new CoreAudioRenderActivity(worker);
        RenderActivityReading reading = await activity.ReadAsync(container, CancellationToken.None).WaitAsync(Timeout);

        string steps = string.Join("; ", reading.Steps.Select(s => s.Step + " " + s.CodeName));
        TestContext.WriteLine("default render endpoint: state " + reading.State + ", active sessions " + reading.ActiveSessions + ", steps " + reading.Steps.Count);
        Assert.IsFalse(reading.Steps.Any(s => !s.Ok), "A read of a real endpoint's sessions failed a step: " + steps);
        Assert.IsTrue(reading.State is RenderActivityState.Silent or RenderActivityState.Playing, "The read did not decide: " + reading.State);
        Assert.AreEqual(reading.State == RenderActivityState.Playing, reading.ActiveSessions > 0);
        Assert.IsFalse(log.Entries.Any(e => e.Level == LogLevel.Error), string.Join(Environment.NewLine, log.Entries.Select(e => e.Message)));
    }

    // The three real pieces together, with the one action that is not read-only made safe: the real audio read, the real
    // Windows Media Controls read, and a pause the safe-mode decorator refuses. Nothing on this machine is paused.
    [TestMethod]
    public async Task TheRealPieceTogetherDecideAnOwnLeaveAndNothingIsPaused()
    {
        var log = new CapturingLog();
        await using var worker = new AudioWorker(log);
        Guid container = await DefaultRenderContainerAsync(worker);
        if (container == Guid.Empty)
        {
            Assert.Inconclusive("This machine has no default render endpoint to decide a leave on.");
            return;
        }

        IMediaSessions sessions = new SafeMediaSessions(new WindowsMediaSessions(log), log);
        using var pause = new PauseOnLeave(sessions, new CoreAudioRenderActivity(worker), () => true, TimeProvider.System, log);
        await pause.OnRender(RenderState.Active, container, DateTimeOffset.UtcNow, changeInFlight: null);

        await pause.BeforeOwnLeaveAsync("Disconnect", container, TimeSpan.FromSeconds(20), CancellationToken.None).WaitAsync(Timeout);
        foreach (LogEntry entry in log.Entries)
        {
            TestContext.WriteLine(entry.Level + " " + entry.Message);
        }

        Assert.IsTrue(
            log.Entries.Any(e => e.Message.StartsWith(PauseOnLeaveText.Prefix, StringComparison.Ordinal)),
            "The decision wrote no line: " + string.Join(" | ", log.Entries.Select(e => e.Message)));
        Assert.IsFalse(log.Entries.Any(e => e.Level == LogLevel.Error), string.Join(Environment.NewLine, log.Entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Message)));
    }

    private static async Task<Guid> DefaultRenderContainerAsync(AudioWorker worker) =>
        await worker.RunAsync(_ =>
        {
            int hr = worker.TryGetEnumerator(out Earshot.Interop.IMMDeviceEnumerator? enumerator);
            if (hr < 0 || enumerator is null)
            {
                return Guid.Empty;
            }

            (Guid container, StepOutcome step) = CoreAudioEndpointReader.ReadDefaultRenderContainer(enumerator, ComRelease.Rcw);
            return step.Ok ? container : Guid.Empty;
        }).WaitAsync(Timeout);
}
