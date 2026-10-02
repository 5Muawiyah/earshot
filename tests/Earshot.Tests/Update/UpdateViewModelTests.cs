using Earshot.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Update;

// The words and shapes of the update states. Version numbers are the real ones passed in, never fixed text, so a
// sample number from a mock-up can never reach a person.
[TestClass]
public sealed class UpdateViewModelTests
{
    private static readonly ReleaseVersion Installed = new(1, 1, 0);
    private static readonly ReleaseVersion Next = new(1, 2, 0);

    private static UpdateViewModel For(UpdateStage stage, ReleaseVersion? available = null, int? percent = null, string? reason = null, string? notice = null) =>
        UpdateViewModel.For(stage, Installed, available, percent, reason, notice);

    private static void AssertView(UpdateViewModel view, UpdateIcon icon, string status, string? sub, params (UpdateButtonRole Role, string Label, bool Primary)[] buttons)
    {
        Assert.AreEqual("Updates", view.Title);
        Assert.AreEqual(icon, view.Icon);
        Assert.AreEqual(status, view.Status);
        Assert.AreEqual(sub, view.Sub);
        CollectionAssert.AreEqual(
            buttons.Select(b => new UpdateButton(b.Role, b.Label, b.Primary)).ToArray(),
            view.Buttons.ToArray());
    }

    // The switch closes this copy through Exit, which hands the AirPods back and blocks them when Hand back is on, so the card
    // says so before the button is pressed.
    [TestMethod]
    public void TheSwitchCardSaysBeforeTheButtonThatSwitchingClosesThisCopyAndHandsTheAirPodsBack()
    {
        UpdateViewModel view = For(UpdateStage.SwitchOffered);

        Assert.AreEqual(UpdateCopy.SwitchNotice, view.Notice);
        StringAssert.Contains(view.Notice, "Hand back");
        StringAssert.Contains(view.Notice, "hands the AirPods back and blocks them");
        Assert.AreEqual(UpdateCopy.SwitchNotice, view.CardText, "It is what a short message card under the status shows too.");
        Assert.AreEqual(UpdateButtonRole.Switch, view.Buttons.Single().Role);
    }

    [TestMethod]
    public void CheckingSpinsAndNamesTheInstalledVersion() =>
        AssertView(For(UpdateStage.Checking), UpdateIcon.Spinner, "Checking for updates", "Version 1.1.0 installed");

    [TestMethod]
    public void UpToDateShowsATickTheVersionAndOffersCheck() =>
        AssertView(For(UpdateStage.UpToDate), UpdateIcon.Check, "You're up to date", "Version 1.1.0", (UpdateButtonRole.Check, "Check", false));

    [TestMethod]
    public void AvailableNamesTheNewVersionAndOffersUpdate() =>
        AssertView(For(UpdateStage.Available, Next), UpdateIcon.Down, "Version 1.2.0 is available", "Version 1.1.0 installed",
            (UpdateButtonRole.Update, "Update", true));

    [TestMethod]
    public void DownloadingNamesTheVersionShowsProgressAndOffersCancel()
    {
        UpdateViewModel view = For(UpdateStage.Downloading, Next, percent: 40);

        AssertView(view, UpdateIcon.Down, "Downloading 1.2.0", null, (UpdateButtonRole.Cancel, "Cancel", false));
        Assert.AreEqual(40, view.ProgressPercent);
    }

    [TestMethod]
    public void DownloadingWithAnUnknownSizeHasNoPercentage() =>
        Assert.IsNull(For(UpdateStage.Downloading, Next, percent: null).ProgressPercent);

    [TestMethod]
    public void ACheckThatFailedSaysSoAndOffersToTryAgain()
    {
        UpdateViewModel view = For(UpdateStage.CheckFailed, reason: "Couldn't reach GitHub. Check your connection.");

        AssertView(view, UpdateIcon.Caution, "Couldn't check for updates", "Version 1.1.0 installed", (UpdateButtonRole.TryAgain, "Try again", true));
        Assert.AreEqual("Couldn't reach GitHub. Check your connection.", view.Reason);
        Assert.AreEqual("Couldn't reach GitHub. Check your connection.", view.CardText, "A short message card shows the plain reason under the headline.");
    }

    [TestMethod]
    public void ADownloadThatFailedSaysSoAndNamesTheVersion() =>
        AssertView(For(UpdateStage.DownloadFailed, Next, reason: "The download stopped part way. Nothing was changed."),
            UpdateIcon.Caution, "Couldn't download the update", "Version 1.2.0", (UpdateButtonRole.TryAgain, "Try again", true));

    [TestMethod]
    public void HandingOverAsksForTheWindowsPromptAndNamesTheVersion() =>
        AssertView(For(UpdateStage.HandingOver, Next), UpdateIcon.Shield, "Approve the Windows prompt", "Installing 1.2.0");

    [TestMethod]
    public void AHandoverThatCouldNotStartHasItsOwnPlainWording() =>
        AssertView(For(UpdateStage.HandoverFailed, Next, reason: "Windows would not start the update (error 5). Nothing was changed."),
            UpdateIcon.Caution, "Couldn't start the update", "Version 1.2.0", (UpdateButtonRole.TryAgain, "Try again", true));

    [TestMethod]
    public void BeforeAnyCheckTheRowNamesTheInstalledVersionAndOffersCheck() =>
        AssertView(For(UpdateStage.Idle), UpdateIcon.None, "Check for updates", "Version 1.1.0", (UpdateButtonRole.Check, "Check", false));

    [TestMethod]
    public void ANoticeRidesOnTheAvailableView()
    {
        UpdateViewModel view = For(UpdateStage.Available, Next, notice: UpdateCopy.PromptDeclinedNotice);

        Assert.AreEqual("The Windows prompt was declined, so nothing was changed.", view.Notice);
        Assert.AreEqual(view.Notice, view.CardText);
    }

    [TestMethod]
    public void EveryStageHasAViewAndNoCopyHoldsAnEmDashOrAFixedVersion()
    {
        foreach (UpdateStage stage in Enum.GetValues<UpdateStage>())
        {
            UpdateViewModel view = For(stage, Next, 10, "reason", "notice");
            foreach (string? text in new[] { view.Title, view.Status, view.Sub, view.Reason, view.Notice }.Concat(view.Buttons.Select(b => b.Label)))
            {
                Assert.IsFalse(text?.Contains((char)0x2014) ?? false, stage + ": " + text);
                Assert.IsFalse(text?.Contains("1.3.2", StringComparison.Ordinal) ?? false, stage + " holds the design's placeholder version: " + text);
                Assert.IsFalse(text?.Contains("1.4.0", StringComparison.Ordinal) ?? false, stage + " holds the design's placeholder version: " + text);
            }
        }
    }
}
