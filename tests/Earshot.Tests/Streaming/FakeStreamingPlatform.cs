using Earshot.Contracts;
using Earshot.Streaming;

namespace Earshot.Tests.Streaming;

// A scripted IStreamingPlatform: the whole of Windows, as far as the streaming tests are concerned. No test in
// this suite touches WinRT, enumerates a real device or opens a real connection except the read-only binding
// proof (WindowsStreamingPlatformBindingTests), which says so itself.
//
// It records every call it received, in order, so a test can assert call order, and it is safe to call from any
// thread, because the coordinator is.
internal sealed class FakeStreamingPlatform : IStreamingPlatform
{
    private readonly Lock _gate = new();
    private readonly List<string> _calls = new();
    private readonly Queue<Func<CancellationToken, Task<StreamingDiscovery>>> _discoveries = new();
    private readonly Dictionary<string, Func<CancellationToken, Task<StreamingEnableOutcome>>> _enables = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<CancellationToken, Task<StreamingOpenOutcome>>> _opens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource> _openEntered = new(StringComparer.Ordinal);
    private readonly HashSet<string> _releaseFails = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _supportEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Func<CancellationToken, Task<StreamingDiscovery>> _lastDiscovery = _ => Task.FromResult(Found());

    public event EventHandler<StreamingLinkChanged>? LinkChanged;

    public StreamingSupport Support { get; set; } = StreamingSupport.Supported;

    public StepOutcome SupportStep { get; set; } = StepOutcomes.FromHResult("streaming-support", 0);

    public int CheckSupportCalls { get; private set; }

    // Whether anything is subscribed, so a test can see the coordinator let go of the event.
    public bool HasLinkSubscribers => LinkChanged is not null;

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToArray();
            }
        }
    }

    public IReadOnlyList<string> CallsNamed(string prefix) => Calls.Where(c => c.StartsWith(prefix, StringComparison.Ordinal)).ToArray();

    public static StreamingDiscovery Found(params StreamingDevice[] devices) =>
        new(StreamingDiscoveryStatus.Ok, devices, StepOutcomes.FromHResult("streaming-list", 0));

    public static StreamingDiscovery ListFailed(int hresult) =>
        new(StreamingDiscoveryStatus.Failed, [], new StepOutcome("streaming-list", false, hresult, NativeCodes.Name(hresult), "COMException"));

    // The next read of the list answers this; once the queue is empty, the last answer repeats.
    public void NextDiscovery(StreamingDiscovery discovery) => NextDiscovery(_ => Task.FromResult(discovery));

    public void NextDiscovery(Func<CancellationToken, Task<StreamingDiscovery>> read)
    {
        lock (_gate)
        {
            _discoveries.Enqueue(read);
        }
    }

    public void EnableAnswers(string deviceId, StreamingEnableStatus status, int hresult = 0) =>
        _enables[deviceId] = _ => Task.FromResult(new StreamingEnableOutcome(
            status,
            deviceId,
            new StepOutcome("streaming-enable", status == StreamingEnableStatus.Enabled, hresult, NativeCodes.Name(hresult), hresult == 0 ? null : "COMException")));

    public void EnableRuns(string deviceId, Func<CancellationToken, Task<StreamingEnableOutcome>> run) => _enables[deviceId] = run;

    public void OpenAnswers(string deviceId, StreamingOpenStatus status, int hresult = 0) =>
        _opens[deviceId] = _ => Task.FromResult(new StreamingOpenOutcome(
            status,
            deviceId,
            new StepOutcome("streaming-open", status == StreamingOpenStatus.Open, hresult, hresult == 0 ? status.ToString() : NativeCodes.Name(hresult), hresult == 0 ? StreamingDetail.NoExtendedError : status.ToString())));

    public void OpenRuns(string deviceId, Func<CancellationToken, Task<StreamingOpenOutcome>> run) => _opens[deviceId] = run;

    // An open that Windows never answers: it ends only when the token it was given is cancelled.
    public void OpenNeverAnswers(string deviceId) =>
        _opens[deviceId] = async token =>
        {
            var never = new TaskCompletionSource<StreamingOpenOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = token.Register(() => never.TrySetCanceled(token));
            return await never.Task.ConfigureAwait(false);
        };

    // Completes once OpenAsync has been called for the device, so a test can wait for that instead of sleeping.
    public Task OpenEntered(string deviceId)
    {
        lock (_gate)
        {
            return EnteredLocked(deviceId).Task;
        }
    }

    public void RaiseLink(string deviceId, StreamingLinkState state) =>
        LinkChanged?.Invoke(this, new StreamingLinkChanged(deviceId, state));

    public StreamingSupportCheck CheckSupport()
    {
        lock (_gate)
        {
            CheckSupportCalls++;
        }

        _supportEntered.TrySetResult();
        SupportHeldUntil?.Wait();
        return new StreamingSupportCheck(Support, SupportStep);
    }

    public Task<StreamingDiscovery> ListStreamCapableDevicesAsync(CancellationToken cancellationToken)
    {
        Func<CancellationToken, Task<StreamingDiscovery>> read;
        lock (_gate)
        {
            _calls.Add("List");
            if (_discoveries.Count > 0)
            {
                _lastDiscovery = _discoveries.Dequeue();
            }

            read = _lastDiscovery;
        }

        return read(cancellationToken);
    }

    public Task<StreamingEnableOutcome> EnableAsync(string deviceId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _calls.Add("Enable(" + deviceId + ")");
        }

        return _enables.TryGetValue(deviceId, out Func<CancellationToken, Task<StreamingEnableOutcome>>? run)
            ? run(cancellationToken)
            : Task.FromResult(new StreamingEnableOutcome(StreamingEnableStatus.Enabled, deviceId, StepOutcomes.FromHResult("streaming-enable", 0)));
    }

    public Task<StreamingOpenOutcome> OpenAsync(string deviceId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _calls.Add("Open(" + deviceId + ")");
            EnteredLocked(deviceId).TrySetResult();
        }

        return _opens.TryGetValue(deviceId, out Func<CancellationToken, Task<StreamingOpenOutcome>>? run)
            ? run(cancellationToken)
            : Task.FromResult(new StreamingOpenOutcome(StreamingOpenStatus.Open, deviceId, StepOutcomes.FromHResult("streaming-open", 0)));
    }

    // Set by a test that wants Windows never to come back from letting go: Release then blocks until it is signalled.
    public ManualResetEventSlim? ReleaseHeldUntil { get; set; }

    // Set by a test that wants the support check, and so the building of a coordinator, held until it says so.
    public ManualResetEventSlim? SupportHeldUntil { get; set; }

    // Completes once CheckSupport has been entered, so a test can wait for that instead of sleeping.
    public Task SupportEntered => _supportEntered.Task;

    // E_UNEXPECTED from Dispose, as the release step a real platform would record for it.
    public static StepOutcome ReleaseFailure { get; } =
        new("streaming-release", Ok: false, unchecked((int)0x8000FFFF), NativeCodes.Name(unchecked((int)0x8000FFFF)), "COMException");

    // Every release of this device fails, as if Windows would not let go, until ReleaseSucceedsAgain is called for it.
    // Like the real platform, a device whose release failed is still held, so a later release tries it again.
    public void ReleaseFails(string deviceId)
    {
        lock (_gate)
        {
            _releaseFails.Add(deviceId);
        }
    }

    public void ReleaseSucceedsAgain(string deviceId)
    {
        lock (_gate)
        {
            _releaseFails.Remove(deviceId);
        }
    }

    public StreamingReleaseOutcome Release(string deviceId)
    {
        bool fails;
        lock (_gate)
        {
            _calls.Add("Release(" + deviceId + ")");
            fails = _releaseFails.Contains(deviceId);
        }

        ReleaseHeldUntil?.Wait();
        return fails
            ? new StreamingReleaseOutcome(false, deviceId, ReleaseFailure)
            : new StreamingReleaseOutcome(true, deviceId, StepOutcomes.FromHResult("streaming-release", 0));
    }

    private TaskCompletionSource EnteredLocked(string deviceId)
    {
        if (!_openEntered.TryGetValue(deviceId, out TaskCompletionSource? entered))
        {
            entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _openEntered[deviceId] = entered;
        }

        return entered;
    }
}

// A busy gate a test opens and closes by hand.
internal sealed class ManualBusyGate : IBusyGate
{
    private volatile bool _busy;

    public bool IsBusy => _busy;

    public string BusyReason => _busy ? "a connect is in flight" : "";

    public void Hold() => _busy = true;

    public void Release() => _busy = false;
}
