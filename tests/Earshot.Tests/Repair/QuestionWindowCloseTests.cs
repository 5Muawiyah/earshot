using System.Diagnostics;
using System.Windows.Forms;
using Earshot.Repair;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Repair;

// The real question windows, shown on a private desktop: Exit while one is up closes it as a cancel, and one left unanswered
// closes itself at its limit as a timeout. Before, only a fake prompter was told to close, and neither window had been shown.
[TestClass]
public sealed class QuestionWindowCloseTests
{
    private static readonly FixPrompt Plain = new("Pair AirPods", "Use these AirPods instead?", "Earshot uses other AirPods now.", [], [], "Use these AirPods");

    private static readonly FixPrompt WithPictures = new(
        "Pair AirPods", "Put your AirPods in pairing mode", PromptPictures.PairingModeSteps, [], [], "Look for AirPods", PromptPictures.PairingMode);

    [TestMethod]
    public void ThePictureQuestionClosesAsACancelWhenEarshotCloses() =>
        AssertAnswer(WithPictures, closeAfter: TimeSpan.FromMilliseconds(300), limit: TimeSpan.FromMinutes(1), FixAnswer.Cancel);

    [TestMethod]
    public void ThePictureQuestionClosesItselfAtItsLimit() =>
        AssertAnswer(WithPictures, closeAfter: null, limit: TimeSpan.FromMilliseconds(300), FixAnswer.TimedOut);

    [TestMethod]
    public void TheTaskDialogQuestionClosesAsACancelWhenEarshotCloses() =>
        AssertAnswer(Plain, closeAfter: TimeSpan.FromMilliseconds(300), limit: TimeSpan.FromMinutes(1), FixAnswer.Cancel);

    [TestMethod]
    public void TheTaskDialogQuestionClosesItselfAtItsLimit() =>
        AssertAnswer(Plain, closeAfter: null, limit: TimeSpan.FromMilliseconds(300), FixAnswer.TimedOut);

    private static void AssertAnswer(FixPrompt prompt, TimeSpan? closeAfter, TimeSpan limit, FixAnswer expected)
    {
        Phase5.CardDesktop.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            using var closing = new CancellationTokenSource();
            if (closeAfter is { } after)
            {
                closing.CancelAfter(after);
            }

            var watch = Stopwatch.StartNew();
            FixPromptAnswer answer = new TaskDialogPrompter { Limit = limit }.Ask(prompt, closing.Token);

            Assert.AreEqual(expected, answer.Answer);
            Assert.IsLessThan(TimeSpan.FromSeconds(20), watch.Elapsed);
        }, TimeSpan.FromSeconds(30));
    }
}
