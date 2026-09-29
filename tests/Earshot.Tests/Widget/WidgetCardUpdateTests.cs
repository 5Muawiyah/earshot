using System.Drawing;
using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Tests.Integration.Coordinator;
using Earshot.Tests.Phase1;
using Earshot.Tests.Update;
using Earshot.Update;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The update line on the main card and the update page: the line appears only when a check found a newer version,
// every state of the flow renders on the page in light and dark, and nothing downloads until the person clicks
// Update on the line (or on the page after a check).
[TestClass]
public sealed class WidgetCardUpdateTests
{
    private static readonly UpdateStage[] Shown =
    [
        UpdateStage.Checking, UpdateStage.UpToDate, UpdateStage.Available, UpdateStage.Downloading, UpdateStage.CheckFailed,
        UpdateStage.DownloadFailed, UpdateStage.HandoverFailed, UpdateStage.HandingOver,
    ];

    // ---- The update line

    [TestMethod]
    public void TheLineAppearsOnlyWhenANewerVersionIsKnown()
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = CardKit.NewCard(dark: false);

            card.Render(CardKit.MainModel(updateVersion: null), 96);
            Assert.IsFalse(card.CurrentMainLayout.ShowUpdateLine, "No newer version known: no line.");
            int without = card.CurrentMainLayout.Height;

            card.Render(CardKit.MainModel(updateVersion: "1.2.0"), 96);
            WidgetCardLayout.Layout layout = card.CurrentMainLayout;
            Assert.IsTrue(layout.ShowUpdateLine);
            Assert.AreEqual(40, layout.UpdateLine.Height);
            Assert.AreEqual(card.ClientSize.Width, layout.UpdateLine.Width, "The line runs the full width of the card.");
            Assert.AreEqual(24, layout.UpdateButton.Height);
            Assert.IsGreaterThan(without, layout.Height, "The card is taller by the line, which sits above the button.");
            Assert.IsGreaterThanOrEqualTo(layout.UpdateLine.Bottom, layout.Button.Top);
            Assert.IsGreaterThan(layout.UpdateCaption.Right - 1, layout.UpdateButton.Left - 1, "The caption and the button do not overlap.");

            card.Render(CardKit.MainModel(updateVersion: null), 96);
            Assert.IsFalse(card.CurrentMainLayout.ShowUpdateLine, "Gone again when nothing newer is known.");
            Assert.AreEqual(without, card.CurrentMainLayout.Height);
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TheLineDrawsItsDividerItsCaptionAndItsButton(bool dark)
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = CardKit.NewCard(dark);
            card.Render(CardKit.MainModel(updateVersion: "1.2.0"), 96);
            using Bitmap bitmap = CardKit.Render(card);
            WidgetCardLayout.Layout layout = card.CurrentMainLayout;
            Color background = bitmap.GetPixel(0, 0);

            Assert.AreEqual("Version 1.2.0 is available", WidgetCopy.UpdateAvailable("1.2.0"));
            Assert.IsTrue(CardKit.HasInk(bitmap, new Rectangle(0, layout.UpdateLine.Top, layout.UpdateLine.Width, 1), background), "The divider above.");
            Assert.IsTrue(CardKit.HasInk(bitmap, layout.UpdateCaption, background), "The caption.");
            Color fill = bitmap.GetPixel(layout.UpdateButton.X + 4, layout.UpdateButton.Y + 12);
            Assert.IsTrue(CardKit.HasInk(bitmap, Rectangle.Inflate(layout.UpdateButton, -6, -6), fill), "The button's word.");
        });
    }

    [TestMethod]
    public void TheCaseOpenCardNeverHasAnUpdateLine()
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard notice = CardKit.NewCard(dark: false, notice: true);
            notice.Render(CardKit.MainModel(updateVersion: "1.2.0"), 96);

            Assert.IsFalse(notice.CurrentMainLayout.ShowUpdateLine);
            Assert.IsTrue(notice.CurrentMainLayout.Gear.IsEmpty, "And no gear.");
        });
    }

    [TestMethod]
    public void TheLineShowsOnAnOpenCardWhenACheckFindsAVersionAndTheButtonIsTheOnlyThingThatStartsIt()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var host = new FakeCardHost();
            var log = new CapturingLog();
            WidgetCard? card = null;
            using var presenter = new WidgetCardPresenter(
                () => card = new WidgetCard(log), CardKit.Callbacks(), CardKit.Inline, new Streaming.TestTimeProvider(), log, host);
            presenter.RequestShow(CardKit.Gauge, CardKit.Gauge.Location);
            Application.DoEvents();
            Assert.IsFalse(card!.CurrentMainLayout.ShowUpdateLine);

            host.AvailableVersion = "1.2.0";
            host.RaiseUpdateChanged();
            Assert.IsTrue(card.CurrentMainLayout.ShowUpdateLine, "The open card picks up the version a check found.");
            Assert.AreEqual(0, host.StartUpdateCalls, "Finding a version downloads nothing.");
            Assert.AreEqual(WidgetCardView.Main, presenter.ViewForTest);

            CardKit.Click(card, card.CurrentMainLayout.UpdateButton);

            Assert.AreEqual(1, host.StartUpdateCalls, "The click on Update starts it.");
            Assert.AreEqual(WidgetCardView.Update, presenter.ViewForTest, "And opens the update page.");
        });
    }

    // ---- The update page: every state

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void EveryStateOfTheUpdateFlowRenders(bool dark)
    {
        Phase5.CardSta.Run(() =>
        {
            string theme = dark ? "dark" : "light";
            Color accent = dark ? WidgetCard.AccentDark : WidgetCard.AccentLight;
            foreach (UpdateStage stage in Shown)
            {
                UpdateViewModel view = CardKit.Update(stage, percent: stage == UpdateStage.Downloading ? 40 : null, reason: stage is UpdateStage.CheckFailed or UpdateStage.DownloadFailed or UpdateStage.HandoverFailed ? "Something plain went wrong." : null);
                string name = theme + " " + stage;
                using WidgetCard card = CardKit.NewCard(dark);
                card.Render(CardKit.UpdateModel(view), 96);
                using Bitmap bitmap = CardKit.Render(card);
                WidgetCardLayout.SetupLayout layout = card.CurrentSetupLayout!;
                Color background = bitmap.GetPixel(0, 0);

                Assert.AreEqual(WidgetCardView.Update, card.EffectiveView, name);
                Assert.AreEqual(360, bitmap.Width, name + ": the same width as every view.");
                Assert.IsTrue(CardKit.HasInk(bitmap, layout.Frame.Back, background), name + ": the back arrow.");
                Assert.IsTrue(CardKit.HasInk(bitmap, layout.Frame.Title, background), name + ": the title.");
                Assert.IsFalse(CardKit.HasInk(bitmap, layout.Frame.Step, background), name + ": no step counter.");
                Assert.IsTrue(CardKit.HasInk(bitmap, layout.StatusIcon, background), name + ": the icon.");
                Assert.IsTrue(CardKit.HasInk(bitmap, layout.StatusText, background), name + ": the status.");
                Assert.AreEqual(view.Status, card.Model.Setup!.Status, name);
                Assert.AreEqual(view.Sub is not null, !layout.StatusSub.IsEmpty, name + ": the sub-line.");
                if (view.Sub is not null)
                {
                    Assert.IsTrue(CardKit.HasInk(bitmap, layout.StatusSub, background), name + ": the sub-line is drawn.");
                }

                Assert.AreEqual(view.Reason is not null, !layout.Caption.IsEmpty, name + ": the cause of a failure.");
                if (view.Reason is not null)
                {
                    Assert.IsTrue(CardKit.HasInk(bitmap, layout.Caption, background), name + ": the cause is drawn.");
                }

                Assert.AreEqual(view.Buttons.Count, layout.Frame.Buttons.Count, name + ": the footer buttons.");
                for (int i = 0; i < layout.Frame.Buttons.Count; i++)
                {
                    Rectangle button = layout.Frame.Buttons[i];
                    Color fill = bitmap.GetPixel(button.X + 4, button.Y + (button.Height / 2));
                    Assert.IsTrue(CardKit.HasInk(bitmap, Rectangle.Inflate(button, -8, -8), fill), name + ": " + view.Buttons[i].Label + " has its word.");
                    Assert.AreEqual(view.Buttons[i].Primary, fill == accent, name + ": " + view.Buttons[i].Label + (view.Buttons[i].Primary ? " is the accent button." : " is a standard button."));
                }

                Assert.AreEqual(stage == UpdateStage.Downloading, !layout.Progress.IsEmpty, name + ": the progress row.");
                if (stage == UpdateStage.Downloading)
                {
                    Rectangle row = layout.Progress;
                    Assert.AreEqual(accent, bitmap.GetPixel(row.X + 4, row.Y + (row.Height / 2)), name + ": the bar is filled in the accent from the left.");
                    Assert.AreNotEqual(accent, bitmap.GetPixel(row.Right - 60, row.Y + (row.Height / 2)), name + ": and is not full at 40%.");
                    Assert.IsTrue(CardKit.HasInk(bitmap, new Rectangle(row.Right - 36, row.Y, 36, row.Height), background), name + ": the percentage at the right.");
                }
            }
        });
    }

    [TestMethod]
    public void ADownloadWhoseSizeIsNotKnownShowsAnEmptyBarAndNoFigure()
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = CardKit.NewCard(dark: false);
            card.Render(CardKit.UpdateModel(CardKit.Update(UpdateStage.Downloading, percent: null)), 96);
            using Bitmap bitmap = CardKit.Render(card);
            Rectangle row = card.CurrentSetupLayout!.Progress;
            Color background = bitmap.GetPixel(0, 0);

            Assert.IsFalse(CardKit.HasInk(bitmap, new Rectangle(row.Right - 36, row.Y, 36, row.Height), background), "No percentage is made up.");
            Assert.AreNotEqual(WidgetCard.AccentLight, bitmap.GetPixel(row.X + 4, row.Y + (row.Height / 2)), "No fill either.");
            Assert.IsTrue(CardKit.HasInk(bitmap, new Rectangle(row.X, row.Y, row.Width - 48, row.Height), background), "The track is still there.");
        });
    }

    [TestMethod]
    public void TheProgressBarIsFourPixelsHighAndFillsInProportion()
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = CardKit.NewCard(dark: false);
            int Filled(int percent)
            {
                card.Render(CardKit.UpdateModel(CardKit.Update(UpdateStage.Downloading, percent)), 96);
                using Bitmap bitmap = CardKit.Render(card);
                Rectangle row = card.CurrentSetupLayout!.Progress;
                int mid = row.Y + (row.Height / 2);
                int count = 0;
                for (int x = row.X; x < row.Right - 48; x++)
                {
                    if (bitmap.GetPixel(x, mid) == WidgetCard.AccentLight)
                    {
                        count++;
                    }
                }

                return count;
            }

            int barWidth = 360 - 32 - 36 - 12;
            Assert.AreEqual(0, Filled(0));
            Assert.AreEqual(barWidth / 2, Filled(50), 3, "Half of the bar at 50%.");
            Assert.AreEqual(barWidth, Filled(100), 3);

            card.Render(CardKit.UpdateModel(CardKit.Update(UpdateStage.Downloading, 50)), 96);
            using Bitmap tall = CardKit.Render(card);
            Rectangle progress = card.CurrentSetupLayout!.Progress;
            int x0 = progress.X + 4;
            int rows = 0;
            for (int y = progress.Top; y < progress.Bottom; y++)
            {
                if (tall.GetPixel(x0, y) == WidgetCard.AccentLight)
                {
                    rows++;
                }
            }

            Assert.AreEqual(4, rows, "4 px high.");
        });
    }

    // ---- The page follows the flow, and Update is the only way to a download

    [TestMethod]
    public void ThePageFollowsTheFlowAndTurnsItsSpinnerOnlyWhileChecking()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var host = new FakeCardHost { View = CardKit.Update(UpdateStage.Checking) };
            var log = new CapturingLog();
            var time = new Streaming.TestTimeProvider();
            WidgetCard? card = null;
            using var presenter = new WidgetCardPresenter(() => card = new WidgetCard(log), CardKit.Callbacks(), CardKit.Inline, time, log, host);
            presenter.RequestShow(CardKit.Gauge, CardKit.Gauge.Location);
            Application.DoEvents();
            CardKit.Click(card!, card!.CurrentMainLayout.Gear);
            CardKit.Click(card, CardKit.Part(card, SettingsRowId.CheckForUpdates, SettingsPart.Button));

            Assert.AreEqual(UpdateCopy.CheckingStatus, card.Model.Setup!.Status);
            Assert.IsTrue(presenter.SpinnerRunningForTest, "Checking turns the spinner.");
            time.Advance(WidgetCardPresenter.SpinnerInterval);
            Assert.AreEqual(1, card.Model.Setup.SpinnerFrame);

            host.View = CardKit.Update(UpdateStage.UpToDate);
            host.RaiseUpdateChanged();
            Assert.AreEqual(UpdateCopy.UpToDateStatus, card.Model.Setup!.Status);
            Assert.IsFalse(presenter.SpinnerRunningForTest, "Nothing turns once the check is over.");

            host.View = CardKit.Update(UpdateStage.Downloading, 70);
            host.RaiseUpdateChanged();
            Assert.AreEqual(70, card.Model.Setup!.ProgressPercent);
            Assert.IsFalse(presenter.SpinnerRunningForTest, "A download shows its bar, not a spinner.");
        });
    }

    [TestMethod]
    public void TheUpdatePagesButtonsCallTheFlowAndBackGoesWhereTheOwnerCameFrom()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var host = new FakeCardHost { View = CardKit.Update(UpdateStage.Available) };
            var log = new CapturingLog();
            WidgetCard? card = null;
            using var presenter = new WidgetCardPresenter(
                () => card = new WidgetCard(log), CardKit.Callbacks(), CardKit.Inline, new Streaming.TestTimeProvider(), log, host);
            presenter.RequestShow(CardKit.Gauge, CardKit.Gauge.Location);
            Application.DoEvents();
            CardKit.Click(card!, card!.CurrentMainLayout.Gear);
            CardKit.Click(card, CardKit.Part(card, SettingsRowId.CheckForUpdates, SettingsPart.Button));
            Assert.AreEqual(0, host.StartUpdateCalls, "A check found a version: nothing is downloaded yet.");

            CardKit.Click(card, card.CurrentSetupLayout!.Frame.Buttons[0]);
            Assert.AreEqual(1, host.StartUpdateCalls, "The page's own Update button starts it.");

            host.View = CardKit.Update(UpdateStage.Downloading, 10);
            host.RaiseUpdateChanged();
            CardKit.Click(card, card.CurrentSetupLayout!.Frame.Buttons[0]);
            Assert.AreEqual(1, host.CancelCalls, "Cancel stops the download.");

            host.View = CardKit.Update(UpdateStage.DownloadFailed, reason: "It stopped.");
            host.RaiseUpdateChanged();
            CardKit.Click(card, card.CurrentSetupLayout!.Frame.Buttons[0]);
            Assert.AreEqual(1, host.TryAgainCalls, "Try again asks the flow again.");
            Assert.AreEqual(1, host.StartUpdateCalls, "Try again is not Update.");

            CardKit.Click(card, card.CurrentSetupLayout!.Frame.Back);
            Assert.AreEqual(WidgetCardView.Settings, presenter.ViewForTest, "Back returns to the settings it was opened from.");
        });
    }

    [TestMethod]
    public void BackFromAPageOpenedByTheUpdateLineReturnsToTheMainCard()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var host = new FakeCardHost { AvailableVersion = "1.2.0", View = CardKit.Update(UpdateStage.Downloading, 5) };
            var log = new CapturingLog();
            WidgetCard? card = null;
            using var presenter = new WidgetCardPresenter(
                () => card = new WidgetCard(log), CardKit.Callbacks(), CardKit.Inline, new Streaming.TestTimeProvider(), log, host);
            presenter.RequestShow(CardKit.Gauge, CardKit.Gauge.Location);
            Application.DoEvents();
            CardKit.Click(card!, card!.CurrentMainLayout.UpdateButton);

            CardKit.Click(card, card.CurrentSetupLayout!.Frame.Back);

            Assert.AreEqual(WidgetCardView.Main, presenter.ViewForTest);
            Assert.IsTrue(presenter.IsShown);
        });
    }

    [TestMethod]
    public void OnlyTheUpdateButtonsStartADownloadNotOpeningSettingsNorACheck()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var host = new FakeCardHost { View = CardKit.Update(UpdateStage.Available) };
            var log = new CapturingLog();
            WidgetCard? card = null;
            using var presenter = new WidgetCardPresenter(
                () => card = new WidgetCard(log), CardKit.Callbacks(), CardKit.Inline, new Streaming.TestTimeProvider(), log, host);
            presenter.RequestShow(CardKit.Gauge, CardKit.Gauge.Location);
            Application.DoEvents();

            CardKit.Click(card!, card!.CurrentMainLayout.Gear);
            CardKit.Click(card, CardKit.Part(card, SettingsRowId.CheckAutomatically, SettingsPart.Toggle));
            CardKit.Click(card, CardKit.Part(card, SettingsRowId.CheckForUpdates, SettingsPart.Button));
            host.RaiseUpdateChanged();
            CardKit.Click(card, card.CurrentSetupLayout!.Frame.Back);
            CardKit.Click(card, card.CurrentSettingsLayout!.Frame.Back);

            Assert.AreEqual(0, host.StartUpdateCalls, "Settings, the automatic switch, a check and going back never start it.");
            Assert.AreEqual(1, host.CheckCalls);
            Assert.AreEqual(0, host.TryAgainCalls);
        });
    }

    // ---- The real tray behind the same seam

    [TestMethod]
    public void TheCardsCheckOnTheRealTrayOnlyChecksAndItsUpdateCallIsTheOnlyDownload()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(source: s =>
            {
                s.OnCheck = _ => Task.FromResult(UpdateCheckResult.Available(FakeUpdateSource.Release("1.2.0")));
                s.OnDownload = (_, _, _) => Task.FromResult(
                    UpdateDownloadResult.Failed(new UpdateFailure(UpdateFailureKind.Network, "It could not be fetched.", "test")));
            });
            IWidgetCardHost host = tray.Context.WidgetCardHostForTest;
            Assert.IsNull(host.AvailableUpdateVersion(), "Nothing is known before a check.");

            host.CheckForUpdates();
            tray.PumpUntilIdle();

            Assert.AreEqual(1, tray.Source.CheckCalls);
            Assert.AreEqual(0, tray.Source.DownloadCalls, "A check never downloads.");
            Assert.AreEqual("1.2.0", host.AvailableUpdateVersion());
            Assert.AreEqual(UpdateCopy.AvailableStatus(new ReleaseVersion(1, 2, 0)), host.UpdatePage(0).Status);
            Assert.IsEmpty(tray.Cards.Shown, "The result is the card's own page, not a message card over it.");

            host.StartUpdate();
            tray.PumpUntilIdle();

            Assert.AreEqual(1, tray.Source.DownloadCalls, "Update is what downloads.");
            Assert.AreEqual(UpdateCopy.DownloadFailedStatus, host.UpdatePage(0).Status);
            Assert.IsEmpty(tray.Launcher.Launches);
        });
    }

    [TestMethod]
    public void TheHostRaisesUpdateChangedWhenTheFlowMoves()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness();
            IWidgetCardHost host = tray.Context.WidgetCardHostForTest;
            int raised = 0;
            host.UpdateChanged += (_, _) => Interlocked.Increment(ref raised);

            host.CheckForUpdates();
            tray.PumpUntilIdle();

            Assert.IsGreaterThanOrEqualTo(2, raised, "Checking, then the result.");
        });
    }
}
