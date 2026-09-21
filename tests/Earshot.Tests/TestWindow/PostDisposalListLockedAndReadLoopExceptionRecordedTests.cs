using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// Two things SafeBeginInvoke's own catch used to leave unguarded: the recorded list it and
// KillActiveRun's own drain can both write to from different threads at once, unlocked; and
// ChildRunner.ReadLoop's own caught IOException/ObjectDisposedException (a killed process's
// broken pipe, expected and never surfaced as a failure) was not recorded anywhere at all,
// contrary to the "never swallowed" this window elsewhere holds itself to.
[TestClass]
public sealed class PostDisposalListLockedAndReadLoopExceptionRecordedTests
{
    [TestMethod]
    public void ConcurrentPostDisposalDeliveriesAreAllRecordedWithoutLoss()
    {
        string repoRoot = RepositoryLocator.RepositoryRoot();
        IReadOnlyList<ManifestRow> rows = Manifest.Load(Path.Combine(repoRoot, "src", "Earshot.TestWindow", "Data", "tests.json"));
        IReadOnlyList<WordingEntry> wording = Wording.Load(Path.Combine(repoRoot, "src", "Earshot.TestWindow", "Data", "wording.json"));
        using var sandbox = new TempFolder();
        var sandboxOptions = new SandboxOptions { Folder = sandbox.Path };

        const int deliveries = 200;
        int recordedCount = -1;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var form = new Earshot.TestWindow.Ui.MainForm(repoRoot, rows, wording, sandboxOptions, @"C:\nowhere\Earshot.exe");
            form.ForceControlCreationForTests();
            form.Dispose();

            try
            {
                Task[] tasks = Enumerable.Range(0, deliveries)
                    .Select(_ => Task.Run(() => form.SafeBeginInvokeForTests(() => { })))
                    .ToArray();
                Task.WaitAll(tasks);
                recordedCount = form.PostDisposalDeliveryFailuresForTests.Count;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "the STA thread did not finish in time.");

        Assert.IsNull(failure, "concurrent SafeBeginInvoke deliveries must never throw.");
        Assert.AreEqual(deliveries, recordedCount,
            "an unlocked list can lose entries under concurrent writers; every one of " + deliveries + " post-disposal deliveries must still be recorded.");
    }

    // A real ChildRunner (never started; nothing here needs a real process), the same private
    // RecordReadLoopException ReadLoop's own two catches call, driven concurrently from many
    // threads the way ReadLoop's own ThreadPool thread and a test reading the list at the same
    // time could race in practice.
    [TestMethod]
    public void ConcurrentReadLoopExceptionsAreAllRecordedWithoutLoss()
    {
        using var runner = new ChildRunner(
            PowerShell51.ExecutablePath(), @"C:\nowhere\driver.ps1", @"C:\nowhere\script.ps1", @"C:\nowhere\Earshot.exe",
            @"C:\nowhere\run", resume: false, variant: 0, offerUninstall: false, allowPlanB: false);

        const int count = 200;
        Task[] tasks = Enumerable.Range(0, count)
            .Select(i => Task.Run(() => runner.RecordReadLoopExceptionForTests(new IOException("probe " + i))))
            .ToArray();
        Task.WaitAll(tasks);

        Assert.AreEqual(count, runner.ReadLoopExceptionsForTests.Count,
            "an unlocked list can lose entries under concurrent writers; every one of " + count + " caught exceptions must still be recorded.");
    }

    [TestMethod]
    public void ARecordedReadLoopExceptionIsKeptExactly()
    {
        using var runner = new ChildRunner(
            PowerShell51.ExecutablePath(), @"C:\nowhere\driver.ps1", @"C:\nowhere\script.ps1", @"C:\nowhere\Earshot.exe",
            @"C:\nowhere\run", resume: false, variant: 0, offerUninstall: false, allowPlanB: false);

        var probe = new ObjectDisposedException("probe");
        runner.RecordReadLoopExceptionForTests(probe);

        Assert.AreEqual(1, runner.ReadLoopExceptionsForTests.Count);
        Assert.AreSame(probe, runner.ReadLoopExceptionsForTests[0]);
    }
}
