using System.Windows.Forms;
using Earshot.Repair;
using Earshot.Tests.Phase1;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Repair;

// The pairing session resumes on pool threads after its awaits, and the re-pair window's form and dialogs belong to the thread
// that made the window. Before, a call from a pool thread made the form there, on a thread with no message loop. Every call now
// runs on the window's own thread. The form itself is not shown here; the work sent is a thread id.
[TestClass]
public sealed class FixRePairWindowThreadTests
{
    [TestMethod]
    public void ACallFromAPoolThreadRunsOnTheThreadThatMadeTheWindow()
    {
        StaThread.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            int windowThread = Environment.CurrentManagedThreadId;
            using var cancel = new CancellationTokenSource();
            using var window = new FixRePairWindow(cancel);

            Task<int> ranOn = Task.Run(() => window.OnUi(() => Environment.CurrentManagedThreadId));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!ranOn.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                Application.DoEvents();
                Thread.Sleep(1);
            }

            Assert.IsTrue(ranOn.IsCompleted, "The call was not run on the window's thread.");
            Assert.AreEqual(windowThread, ranOn.Result);
        });
    }

    // The person closing the window is their Cancel; the run closing it is not. The window is shown, so on a private desktop:
    // run 115 found it shown on the runner's own desktop when this ran on a plain STA thread.
    [TestMethod]
    public void ClosingTheWindowCancelsTheRunAndTheRunsOwnCloseDoesNot()
    {
        Phase5.CardDesktop.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            using var byPerson = new CancellationTokenSource();
            using var window = new FixRePairWindow(byPerson);
            window.ShowInstruction("Looking");
            window.FormForTests!.Close();
            Application.DoEvents();
            Assert.IsTrue(byPerson.IsCancellationRequested, "Closing the window did not cancel the run.");

            using var byRun = new CancellationTokenSource();
            using var other = new FixRePairWindow(byRun);
            other.ShowInstruction("Looking");
            other.Close();
            Application.DoEvents();
            Assert.IsFalse(byRun.IsCancellationRequested, "The run's own close cancelled it.");
        });
    }

    // Fix resumes on a pool thread after the administrator run, and its second question is a window with pictures: the question
    // is asked on the window's thread, as the pick and the number questions are.
    [TestMethod]
    public void AQuestionAskedFromAPoolThreadIsAskedOnTheWindowsThread()
    {
        StaThread.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            int windowThread = Environment.CurrentManagedThreadId;
            using var cancel = new CancellationTokenSource();
            using var window = new FixRePairWindow(cancel);
            var inner = new ThreadRecordingPrompter();
            IFixPrompter prompter = window.AskingOnThisThread(inner);
            var prompt = new FixPrompt("t", "h", "b", [], [], "OK");

            Task<FixPromptAnswer> asked = Task.Run(() => prompter.Ask(prompt, CancellationToken.None));
            Task<FixPromptAnswer> askedPlain = Task.Run(() => prompter.Ask(prompt));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!(asked.IsCompleted && askedPlain.IsCompleted) && watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                Application.DoEvents();
                Thread.Sleep(1);
            }

            Assert.IsTrue(asked.IsCompleted && askedPlain.IsCompleted, "The questions were not asked on the window's thread.");
            CollectionAssert.AreEqual(new[] { windowThread, windowThread }, inner.AskedOn.ToArray());
        });
    }

    private sealed class ThreadRecordingPrompter : IFixPrompter
    {
        public System.Collections.Concurrent.ConcurrentQueue<int> AskedOn { get; } = new();

        public FixPromptAnswer Ask(FixPrompt prompt) => Ask(prompt, CancellationToken.None);

        public FixPromptAnswer Ask(FixPrompt prompt, CancellationToken closeOn)
        {
            AskedOn.Enqueue(Environment.CurrentManagedThreadId);
            return new FixPromptAnswer(FixAnswer.Cancel, -1);
        }
    }

    [TestMethod]
    public void ACallOnTheWindowsOwnThreadRunsAtOnce()
    {
        StaThread.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            using var cancel = new CancellationTokenSource();
            using var window = new FixRePairWindow(cancel);

            Assert.AreEqual(Environment.CurrentManagedThreadId, window.OnUi(() => Environment.CurrentManagedThreadId));
        });
    }
}
