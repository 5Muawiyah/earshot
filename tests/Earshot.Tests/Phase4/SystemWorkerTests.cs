using Earshot.Boot;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase4;

[TestClass]
public sealed class SystemWorkerTests
{
    [TestMethod]
    public async Task WorkRunsOnOneBackgroundMtaThread()
    {
        using var worker = new SystemWorker(new CapturingLog());

        (int Id, ApartmentState Apartment, bool Background) first = await worker.RunAsync(_ =>
            (Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState(), Thread.CurrentThread.IsBackground));
        int second = await worker.RunAsync(_ => Environment.CurrentManagedThreadId);

        Assert.AreEqual(ApartmentState.MTA, first.Apartment);
        Assert.IsTrue(first.Background);
        Assert.AreEqual(first.Id, second);
        Assert.AreEqual(first.Id, worker.ThreadId);
        Assert.AreNotEqual(Environment.CurrentManagedThreadId, first.Id);
    }

    [TestMethod]
    public async Task WorkItemsNeverOverlap()
    {
        using var worker = new SystemWorker(new CapturingLog());
        int running = 0;
        int maximum = 0;

        Task<int>[] items = Enumerable.Range(0, 20).Select(i => worker.RunAsync(_ =>
        {
            int now = Interlocked.Increment(ref running);
            maximum = Math.Max(maximum, now);
            Thread.Sleep(2);
            Interlocked.Decrement(ref running);
            return i;
        })).ToArray();
        int[] results = await Task.WhenAll(items);

        Assert.AreEqual(1, maximum);
        CollectionAssert.AreEqual(Enumerable.Range(0, 20).ToArray(), results, "Run in the order queued.");
    }

    [TestMethod]
    public async Task AnExceptionFaultsTheCallersTask()
    {
        using var worker = new SystemWorker(new CapturingLog());

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => worker.RunAsync<int>(_ => throw new InvalidOperationException("boom")));
        Assert.AreEqual(7, await worker.RunAsync(_ => 7), "The worker keeps running.");
    }

    [TestMethod]
    public async Task ACancelledTokenDoesNotRunTheWork()
    {
        using var worker = new SystemWorker(new CapturingLog());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        bool ran = false;

        Task<int> task = worker.RunAsync(_ =>
        {
            ran = true;
            return 1;
        }, cts.Token);

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => task);
        Assert.IsFalse(ran);
    }

    [TestMethod]
    public async Task WorkThatObservesCancellationEndsCancelled()
    {
        using var worker = new SystemWorker(new CapturingLog());
        using var cts = new CancellationTokenSource();

        Task<int> task = worker.RunAsync(ct =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return 1;
        }, cts.Token);

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => task);
    }

    [TestMethod]
    public void AfterDisposeNoWorkIsAccepted()
    {
        var worker = new SystemWorker(new CapturingLog());
        worker.Dispose();
        worker.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => worker.RunAsync(_ => 1));
        Assert.IsNull(worker.ThreadId, "A worker never used never starts a thread.");
    }
}
