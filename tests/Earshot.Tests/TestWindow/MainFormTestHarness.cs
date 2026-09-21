using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;

namespace Earshot.Tests.TestWindow;

// Drives a real MainForm headlessly: constructed, its Win32 handle forced into existence, never
// Shown. Runs the whole test body on a dedicated STA thread pumping its own message loop with
// Application.DoEvents, since Control.BeginInvoke (every ChildRunner event MainForm subscribes
// to) only ever runs when something pumps that thread's queue. Review round 1's own rule: a
// predicate proved only in isolation proves nothing about the caller that used to bypass it, so
// B1 and B2's own tests drive the real button click handlers, not a copy of their logic.
internal static class MainFormTestHarness
{
    internal static void Run(string sandboxFolder, Action<MainForm> body, TimeSpan? timeout = null)
    {
        string repoRoot = RepositoryLocator.RepositoryRoot();
        IReadOnlyList<ManifestRow> rows = Manifest.Load(Path.Combine(repoRoot, "src", "Earshot.TestWindow", "Data", "tests.json"));
        IReadOnlyList<WordingEntry> wording = Wording.Load(Path.Combine(repoRoot, "src", "Earshot.TestWindow", "Data", "wording.json"));
        var sandbox = new SandboxOptions { Folder = sandboxFolder };

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainForm? form = null;
            try
            {
                form = new MainForm(repoRoot, rows, wording, sandbox, @"C:\nowhere\Earshot.exe");
                form.ForceControlCreationForTests(); // real handles for the form and every child control.
                body(form);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                try
                {
                    if (form?.ActiveRunnerForTests is not null)
                    {
                        form.KillActiveRunForTests();
                    }

                    form?.Dispose();
                }
                catch
                {
                    // Cleanup only; the real failure (if any) is already captured above.
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(timeout ?? TimeSpan.FromSeconds(60)))
        {
            throw new TimeoutException("MainFormTestHarness's STA thread did not finish in time.");
        }

        if (failure is not null)
        {
            throw new InvalidOperationException("MainFormTestHarness body failed: " + failure, failure);
        }
    }

    // Pumps the current thread's message queue (Application.DoEvents) until condition is true or
    // the timeout elapses. Must be called from the same STA thread that created the form.
    internal static void PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(20);
        }
    }
}
