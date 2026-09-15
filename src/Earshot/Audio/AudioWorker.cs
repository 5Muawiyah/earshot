using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Audio;

// The one apartment for Core Audio, DeviceTopology and IKsControl work: a dedicated MTA thread draining a
// serial queue.
//
// Why. Those interfaces are [local] with no proxy/stub and their objects are not agile, so an RCW is only
// usable from the apartment that created it; used from another apartment it throws InvalidCastException
// (E_NOINTERFACE). Their thread safety is undocumented, so calls are also serialised.
// https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immnotificationclient
//
// Rules.
//   - The worker creates and owns the IMMDeviceEnumerator (TryGetEnumerator) and every RCW made from it.
//     Work items create, use and release their RCWs inside the item. Only plain data leaves the worker:
//     a work item that returns a COM object faults its task instead.
//   - The thread starts on the first RunAsync, so building the services costs no thread.
//   - Items run one at a time, in the order they were queued. RunAsync called on the worker thread
//     runs the work inline, so worker code cannot deadlock on its own queue.
//   - Cancellation removes an item that has not started (its task is cancelled). Once started, the work
//     sees the token and decides; it is never torn down mid-call. An OperationCanceledException for the
//     item's own token cancels the task; any other exception faults it, so the awaiting caller sees it.
//   - Task continuations never run on the worker thread.
//   - DisposeAsync lets queued items finish, then on the worker unregisters the notification client (if
//     one is registered) and releases the enumerator, then stops the thread. RunAsync after that returns
//     a faulted task (ObjectDisposedException).
internal sealed class AudioWorker : IAudioWorker
{
    internal const string ThreadName = "Earshot audio worker";

    private readonly ILog _log;
    private readonly Lock _gate = new();
    private readonly BlockingCollection<WorkItem> _queue = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _thread;
    private int _threadId;
    private bool _disposing;
    private Task? _disposeTask;

    // Owned by the worker thread; touched only there.
    private IMMDeviceEnumerator? _enumerator;
    private NotificationClient? _client;
    private bool _closed;

    public AudioWorker(ILog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    // True on the worker thread.
    internal bool IsWorkerThread
    {
        get
        {
            int id = Volatile.Read(ref _threadId);
            return id != 0 && id == Environment.CurrentManagedThreadId;
        }
    }

    // True once the thread has started.
    internal bool HasStarted
    {
        get { lock (_gate) { return _thread is not null; } }
    }

    // Completes when the worker thread has exited (never, if it never started).
    internal Task Stopped => _stopped.Task;

    public Task<T> RunAsync<T>(Func<CancellationToken, T> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(ct);
        }

        var item = new WorkItem<T>(work, ct);
        if (IsWorkerThread)
        {
            if (_closed)
            {
                return Task.FromException<T>(new ObjectDisposedException(nameof(AudioWorker)));
            }

            item.Execute();
            return item.Task;
        }

        lock (_gate)
        {
            if (_disposing)
            {
                item.Abandon();
                return Task.FromException<T>(new ObjectDisposedException(nameof(AudioWorker)));
            }

            StartThreadLocked();
            _queue.Add(item, CancellationToken.None);
        }

        return item.Task;
    }

    public Task RunAsync(Action<CancellationToken> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return RunAsync<object?>(token =>
        {
            work(token);
            return null;
        }, ct);
    }

    public ValueTask DisposeAsync()
    {
        if (IsWorkerThread)
        {
            // Waiting for the thread to stop from the thread itself can never finish.
            return ValueTask.FromException(new InvalidOperationException("The audio worker cannot be disposed from its own thread."));
        }

        Task task;
        lock (_gate)
        {
            if (_disposeTask is null)
            {
                _disposing = true;
                if (_thread is null)
                {
                    _queue.CompleteAdding();
                    _queue.Dispose();
                    _disposeTask = Task.CompletedTask;
                }
                else
                {
                    var shutdown = new WorkItem<object?>(_ =>
                    {
                        ShutdownOnWorker();
                        return null;
                    }, CancellationToken.None);
                    _queue.Add(shutdown);
                    _queue.CompleteAdding();
                    _disposeTask = FinishDisposeAsync(shutdown.Task);
                }
            }

            task = _disposeTask;
        }

        return new ValueTask(task);
    }

    // Worker thread only. The enumerator, created on first use with CoCreateInstance. It stays owned by the
    // worker, which releases it when it stops: never release it. Returns the HRESULT; enumerator is null on
    // failure. A failed creation is tried again on the next call.
    // https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-events
    internal int TryGetEnumerator(out IMMDeviceEnumerator? enumerator)
    {
        ThrowIfNotUsable();
        if (_enumerator is null)
        {
            int hr = CoreAudio.TryCreateEnumerator(out IMMDeviceEnumerator? created);
            if (hr < 0 || created is null)
            {
                enumerator = null;
                return hr < 0 ? hr : CoreAudio.E_POINTER;
            }

            _enumerator = created;
        }

        enumerator = _enumerator;
        return 0;
    }

    // Worker thread only. Registers the client for endpoint notifications. The client is held in a worker
    // field from before Register until after Unregister, because MMDevAPI does not AddRef it. Only one
    // client is registered at a time.
    // https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdeviceenumerator-registerendpointnotificationcallback
    internal StepOutcome RegisterNotificationClient(NotificationClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        ThrowIfNotUsable();
        if (_client is not null)
        {
            return StepOutcomes.NotAttempted(Steps.RegisterClient, "A notification client is already registered on this worker.");
        }

        int hr = TryGetEnumerator(out IMMDeviceEnumerator? enumerator);
        if (hr < 0 || enumerator is null)
        {
            return StepOutcomes.FromHResult(Steps.CreateEnumerator, hr);
        }

        _client = client;
        hr = enumerator.RegisterEndpointNotificationCallback(client);
        if (hr < 0)
        {
            _client = null;
        }

        return StepOutcomes.FromHResult(Steps.RegisterClient, hr);
    }

    // Worker thread only. Unregisters the registered client, if any, and only then lets go of it.
    // https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdeviceenumerator-unregisterendpointnotificationcallback
    internal StepOutcome UnregisterNotificationClient()
    {
        if (!IsWorkerThread)
        {
            throw new InvalidOperationException(NotOnWorkerMessage);
        }

        if (_client is null)
        {
            return StepOutcomes.NotAttempted(Steps.UnregisterClient, "No notification client is registered.");
        }

        if (_enumerator is null)
        {
            // Cannot happen: a registered client implies an enumerator. Reported rather than assumed.
            return StepOutcomes.NotAvailable(Steps.UnregisterClient, "The enumerator is gone; the client cannot be unregistered.");
        }

        NotificationClient client = _client;
        int hr = _enumerator.UnregisterEndpointNotificationCallback(client);
        ReportCallbackFailures(client);
        _client = null;
        GC.KeepAlive(client);
        return StepOutcomes.FromHResult(Steps.UnregisterClient, hr);
    }

    // Worker thread only. The registered client, for reading its callback failure counters.
    internal NotificationClient? RegisteredClient
    {
        get
        {
            ThrowIfNotUsable();
            return _client;
        }
    }

    internal static class Steps
    {
        public const string CreateEnumerator = "create-enumerator";
        public const string RegisterClient = "register-notification-client";
        public const string UnregisterClient = "unregister-notification-client";
    }

    private const string NotOnWorkerMessage =
        "Core Audio objects belong to the audio worker thread. Call this inside RunAsync.";

    private void ThrowIfNotUsable()
    {
        if (!IsWorkerThread)
        {
            throw new InvalidOperationException(NotOnWorkerMessage);
        }

        ObjectDisposedException.ThrowIf(_closed, this);
    }

    private void StartThreadLocked()
    {
        if (_thread is not null)
        {
            return;
        }

        var thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = ThreadName,
        };
        thread.SetApartmentState(ApartmentState.MTA);
        Volatile.Write(ref _threadId, thread.ManagedThreadId);
        thread.Start();
        _thread = thread;
    }

    private void ThreadMain()
    {
        try
        {
            foreach (WorkItem item in _queue.GetConsumingEnumerable())
            {
                item.Execute();
            }
        }
        finally
        {
            _stopped.TrySetResult();
        }
    }

    private void ShutdownOnWorker()
    {
        if (_client is not null)
        {
            StepOutcome step = UnregisterNotificationClient();
            _log.Write(step.Ok ? LogLevel.Info : LogLevel.Error,
                "Audio worker stopping: " + step.Step + " " + step.CodeName + (step.Detail is null ? "" : " (" + step.Detail + ")"));
        }

        if (_enumerator is not null)
        {
            Marshal.ReleaseComObject(_enumerator);
            _enumerator = null;
        }

        _closed = true;
    }

    private async Task FinishDisposeAsync(Task shutdown)
    {
        try
        {
            await shutdown.ConfigureAwait(false);
        }
        finally
        {
            await _stopped.Task.ConfigureAwait(false);
            _queue.Dispose();
        }
    }

    private void ReportCallbackFailures(NotificationClient client)
    {
        (int count, Exception? last) = client.TakeSinkFailures();
        if (count > 0)
        {
            _log.Error(count + " endpoint notifications could not be passed on.", last);
        }
    }

    private abstract class WorkItem
    {
        public abstract void Execute();
    }

    private sealed class WorkItem<T> : WorkItem
    {
        private const int Queued = 0;
        private const int Running = 1;
        private const int Done = 2;

        private readonly Func<CancellationToken, T> _work;
        private readonly CancellationToken _ct;
        private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _registration;
        private int _state;

        public WorkItem(Func<CancellationToken, T> work, CancellationToken ct)
        {
            _work = work;
            _ct = ct;
            if (ct.CanBeCanceled)
            {
                _registration = ct.Register(static state => ((WorkItem<T>)state!).CancelIfQueued(), this);
            }
        }

        public Task<T> Task => _tcs.Task;

        public override void Execute()
        {
            if (Interlocked.CompareExchange(ref _state, Running, Queued) != Queued)
            {
                return;
            }

            _registration.Dispose();
            try
            {
                T result = _work(_ct);
                if (result is not null && Marshal.IsComObject(result))
                {
                    _tcs.TrySetException(new InvalidOperationException(
                        "A work item returned a COM object. Only plain data may leave the audio worker."));
                }
                else
                {
                    _tcs.TrySetResult(result);
                }
            }
            catch (OperationCanceledException ex) when (_ct.IsCancellationRequested && ex.CancellationToken == _ct)
            {
                _tcs.TrySetCanceled(_ct);
            }
            catch (Exception ex)
            {
                // Handed to the awaiting caller, not swallowed.
                _tcs.TrySetException(ex);
            }
            finally
            {
                Volatile.Write(ref _state, Done);
            }
        }

        // The item was never queued.
        public void Abandon()
        {
            Volatile.Write(ref _state, Done);
            _registration.Dispose();
        }

        private void CancelIfQueued()
        {
            if (Interlocked.CompareExchange(ref _state, Done, Queued) == Queued)
            {
                _tcs.TrySetCanceled(_ct);
            }
        }
    }
}
