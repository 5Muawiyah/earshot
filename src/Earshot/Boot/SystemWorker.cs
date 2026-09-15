using System.Collections.Concurrent;
using Earshot.Contracts;

namespace Earshot.Boot;

internal interface ISystemWorker
{
    // Runs work on the worker thread, one item at a time, and completes with its result or exception. The
    // token passed to work is the caller's; work that has started decides itself what cancellation means.
    Task<T> RunAsync<T>(Func<CancellationToken, T> work, CancellationToken ct = default);
}

// One background thread in the COM multithreaded apartment for the slow tray-side operations: Task Scheduler
// RunEx and its completion polling, the read-only task checks and the CfgMgr32 verification reads. They never
// touch the UI thread or the audio worker, and they never overlap, so a disable or enable never races a
// service state change requested through the same gate. The thread starts on first use and is a background
// thread, so it never keeps the process alive.
// https://learn.microsoft.com/en-us/dotnet/api/system.threading.thread.setapartmentstate
// https://learn.microsoft.com/en-us/windows/win32/taskschd/boot-trigger-example--c---
internal sealed class SystemWorker : ISystemWorker, IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly ILog _log;
    private readonly Lock _gate = new();
    private Thread? _thread;
    private bool _disposed;

    public SystemWorker(ILog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    // The managed id of the worker thread once it has started, for tests and diagnostics.
    public int? ThreadId { get; private set; }

    public Task<T> RunAsync<T>(Func<CancellationToken, T> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(ct);
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Item()
        {
            if (ct.IsCancellationRequested)
            {
                completion.TrySetCanceled(ct);
                return;
            }

            try
            {
                completion.TrySetResult(work(ct));
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken == ct)
            {
                completion.TrySetCanceled(ct);
            }
            catch (Exception ex)
            {
                // Not swallowed: the caller's task faults with it.
                completion.TrySetException(ex);
            }
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureStarted();

            // Queueing is not cancellable: the item itself sees the token and completes as cancelled.
            _queue.Add(Item, CancellationToken.None);
        }

        return completion.Task;
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _queue.CompleteAdding();
            thread = _thread;
        }

        if (thread is null)
        {
            _queue.Dispose();
            return;
        }

        if (thread.ManagedThreadId == Environment.CurrentManagedThreadId)
        {
            // Disposed from its own work item: the loop ends once that item returns.
            return;
        }

        if (!thread.Join(TimeSpan.FromSeconds(10)))
        {
            _log.Warn("The system worker did not stop within 10 seconds; it is a background thread and ends with the process.");
            return;
        }

        _queue.Dispose();
    }

    private void EnsureStarted()
    {
        if (_thread is not null)
        {
            return;
        }

        var thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "Earshot system worker",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        _thread = thread;
    }

    private void Loop()
    {
        ThreadId = Environment.CurrentManagedThreadId;
        foreach (Action item in _queue.GetConsumingEnumerable())
        {
            item();
        }
    }
}
