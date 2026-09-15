using System.Runtime.InteropServices;
using Earshot.Audio;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase2;

// The worker's queue and thread behaviour, with plain managed work. Only the last test creates a COM
// object (the MMDevice enumerator), to prove an RCW cannot be returned from the worker.
[TestClass]
public sealed class AudioWorkerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task WorkRunsOnOneDedicatedMtaThread()
    {
        var log = new CapturingLog();
        await using var worker = new AudioWorker(log);

        (int Id, ApartmentState Apartment, bool Background, string? Name, bool OnWorker) first =
            await worker.RunAsync(_ => Describe(worker)).WaitAsync(Timeout);
        (int Id, ApartmentState Apartment, bool Background, string? Name, bool OnWorker) second =
            await worker.RunAsync(_ => Describe(worker)).WaitAsync(Timeout);

        Assert.AreEqual(ApartmentState.MTA, first.Apartment);
        Assert.IsTrue(first.Background);
        Assert.AreEqual(AudioWorker.ThreadName, first.Name);
        Assert.IsTrue(first.OnWorker);
        Assert.AreEqual(first.Id, second.Id);
        Assert.AreNotEqual(Environment.CurrentManagedThreadId, first.Id);
        Assert.IsFalse(worker.IsWorkerThread);
    }

    [TestMethod]
    public async Task TheThreadStartsOnlyWhenWorkArrives()
    {
        var worker = new AudioWorker(new CapturingLog());
        Assert.IsFalse(worker.HasStarted);

        await worker.DisposeAsync();

        Assert.IsFalse(worker.HasStarted);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => worker.RunAsync(_ => 1));
    }

    [TestMethod]
    public async Task ItemsRunOneAtATimeInQueueOrder()
    {
        await using var worker = new AudioWorker(new CapturingLog());
        var order = new List<int>();
        int running = 0;
        int overlaps = 0;
        using var gate = new ManualResetEventSlim(false);

        Task blocker = worker.RunAsync(token => gate.Wait(Timeout, token));
        var tasks = new List<Task>();
        for (int i = 0; i < 50; i++)
        {
            int n = i;
            tasks.Add(worker.RunAsync(_ =>
            {
                if (Interlocked.Increment(ref running) != 1)
                {
                    Interlocked.Increment(ref overlaps);
                }

                order.Add(n);
                Thread.Sleep(n % 7 == 0 ? 2 : 0);
                Interlocked.Decrement(ref running);
            }));
        }

        gate.Set();
        await Task.WhenAll(tasks.Append(blocker)).WaitAsync(Timeout);

        Assert.AreEqual(0, overlaps);
        CollectionAssert.AreEqual(Enumerable.Range(0, 50).ToList(), order);
    }

    [TestMethod]
    public async Task AnExceptionReachesTheAwaitingCaller()
    {
        await using var worker = new AudioWorker(new CapturingLog());

        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => worker.RunAsync<int>(_ => throw new InvalidOperationException("boom")));
        Assert.AreEqual("boom", thrown.Message);

        // The worker keeps going after a failed item.
        Assert.AreEqual(7, await worker.RunAsync(_ => 7).WaitAsync(Timeout));
    }

    [TestMethod]
    public async Task CancellingBeforeTheItemStartsSkipsIt()
    {
        await using var worker = new AudioWorker(new CapturingLog());
        using var gate = new ManualResetEventSlim(false);
        using var cts = new CancellationTokenSource();
        bool ran = false;

        Task blocker = worker.RunAsync(token => gate.Wait(Timeout, token));
        Task queued = worker.RunAsync(_ => ran = true, cts.Token);
        cts.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => queued.WaitAsync(Timeout));
        gate.Set();
        await blocker.WaitAsync(Timeout);
        await worker.RunAsync(_ => { }).WaitAsync(Timeout);
        Assert.IsFalse(ran);
    }

    [TestMethod]
    public async Task AnAlreadyCancelledTokenNeverQueues()
    {
        await using var worker = new AudioWorker(new CapturingLog());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Task<int> task = worker.RunAsync(_ => 1, cts.Token);

        Assert.IsTrue(task.IsCanceled);
        Assert.IsFalse(worker.HasStarted);
    }

    [TestMethod]
    public async Task WorkThatStartedSeesTheTokenAndDecides()
    {
        await using var worker = new AudioWorker(new CapturingLog());
        using var started = new ManualResetEventSlim(false);
        using var cts = new CancellationTokenSource();

        Task<string> task = worker.RunAsync(token =>
        {
            started.Set();
            while (!token.IsCancellationRequested)
            {
                Thread.Sleep(1);
            }

            return "finished after cancel";
        }, cts.Token);

        Assert.IsTrue(started.Wait(Timeout));
        cts.Cancel();

        Assert.AreEqual("finished after cancel", await task.WaitAsync(Timeout));
    }

    [TestMethod]
    public async Task OperationCanceledForTheItemTokenCancelsTheTask()
    {
        await using var worker = new AudioWorker(new CapturingLog());
        using var started = new ManualResetEventSlim(false);
        using var cts = new CancellationTokenSource();

        Task task = worker.RunAsync(token =>
        {
            started.Set();
            token.WaitHandle.WaitOne(Timeout);
            token.ThrowIfCancellationRequested();
        }, cts.Token);

        Assert.IsTrue(started.Wait(Timeout));
        cts.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => task.WaitAsync(Timeout));
    }

    [TestMethod]
    public async Task ContinuationsDoNotRunOnTheWorkerThread()
    {
        await using var worker = new AudioWorker(new CapturingLog());
        int workerId = await worker.RunAsync(_ => Environment.CurrentManagedThreadId).WaitAsync(Timeout);

        int continuationId = await worker.RunAsync(_ => 0)
            .ContinueWith(_ => Environment.CurrentManagedThreadId, TaskContinuationOptions.ExecuteSynchronously)
            .WaitAsync(Timeout);

        Assert.AreNotEqual(workerId, continuationId);
    }

    [TestMethod]
    public async Task RunAsyncFromTheWorkerThreadRunsInline()
    {
        await using var worker = new AudioWorker(new CapturingLog());

        (bool completedInline, int value) = await worker.RunAsync(token =>
        {
            Task<int> nested = worker.RunAsync(_ => 42, token);
            return (nested.IsCompleted, nested.Result);
        }).WaitAsync(Timeout);

        Assert.IsTrue(completedInline);
        Assert.AreEqual(42, value);
    }

    [TestMethod]
    public async Task DisposeLetsQueuedWorkFinishThenStopsTheThread()
    {
        var worker = new AudioWorker(new CapturingLog());
        using var gate = new ManualResetEventSlim(false);
        int done = 0;

        Task blocker = worker.RunAsync(token => gate.Wait(Timeout, token));
        Task queued = worker.RunAsync(_ => Interlocked.Increment(ref done));
        ValueTask disposing = worker.DisposeAsync();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => worker.RunAsync(_ => 0));
        gate.Set();
        await disposing.AsTask().WaitAsync(Timeout);

        await Task.WhenAll(blocker, queued).WaitAsync(Timeout);
        Assert.AreEqual(1, done);
        Assert.IsTrue(worker.Stopped.IsCompleted);

        // Disposing again is harmless.
        await worker.DisposeAsync();
    }

    [TestMethod]
    public async Task DisposingFromTheWorkerThreadIsRefused()
    {
        await using var worker = new AudioWorker(new CapturingLog());

        bool refused = await worker.RunAsync(_ =>
        {
            ValueTask attempt = worker.DisposeAsync();
            return attempt.IsFaulted;
        }).WaitAsync(Timeout);

        Assert.IsTrue(refused);
    }

    [TestMethod]
    public async Task WorkerOnlyMembersRefuseOtherThreads()
    {
        await using var worker = new AudioWorker(new CapturingLog());

        Assert.ThrowsExactly<InvalidOperationException>(() => worker.TryGetEnumerator(out _));
        Assert.ThrowsExactly<InvalidOperationException>(() => worker.RegisterNotificationClient(new NotificationClient(_ => { })));
        Assert.ThrowsExactly<InvalidOperationException>(() => worker.UnregisterNotificationClient());
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = worker.RegisteredClient);
    }

    [TestMethod]
    public async Task UnregisterWithNothingRegisteredIsNotAttempted()
    {
        await using var worker = new AudioWorker(new CapturingLog());

        Earshot.Contracts.StepOutcome step = await worker.RunAsync(_ => worker.UnregisterNotificationClient()).WaitAsync(Timeout);

        Assert.IsFalse(step.Ok);
        Assert.AreEqual(Earshot.Contracts.NativeCodes.NotAttempted, step.Code);
    }

    // Creates the MMDevice enumerator (no device is opened) to have a real RCW to return.
    [TestMethod]
    [TestCategory("ReadOnlyHardware")]
    public async Task AComObjectCannotLeaveTheWorker()
    {
        await using var worker = new AudioWorker(new CapturingLog());

        Task<object> leak = worker.RunAsync<object>(_ =>
        {
            int hr = worker.TryGetEnumerator(out Earshot.Interop.IMMDeviceEnumerator? enumerator);
            if (hr < 0 || enumerator is null)
            {
                return "no enumerator: " + Earshot.Contracts.NativeCodes.Name(hr);
            }

            Assert.IsTrue(Marshal.IsComObject(enumerator));
            return enumerator;
        });

        try
        {
            object result = await leak.WaitAsync(Timeout);
            Assert.Inconclusive("The enumerator could not be created: " + result);
        }
        catch (InvalidOperationException ex)
        {
            StringAssert.Contains(ex.Message, "Only plain data may leave the audio worker.");
        }
    }

    private static (int Id, ApartmentState Apartment, bool Background, string? Name, bool OnWorker) Describe(AudioWorker worker) =>
        (Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState(), Thread.CurrentThread.IsBackground,
         Thread.CurrentThread.Name, worker.IsWorkerThread);
}
