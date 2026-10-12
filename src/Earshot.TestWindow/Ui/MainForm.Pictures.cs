using Earshot.TestWindow.Core;

namespace Earshot.TestWindow.Ui;

// The window put into each state the picture renderer draws, from data handed in. Nothing here starts a run, reads a device or
// touches the machine's live-test folders: the form is built over a throwaway sandbox folder and fed text.
internal sealed partial class MainForm
{
    internal enum PictureView { Home, List, RunAll, Step, StepWaiting, Pass, Fail, HandOver }

    // What a picture needs besides the view: a prompt for the step views, a result for pass and fail, and the lines
    // Run all and the hand-over would have computed.
    internal sealed record PictureContent(
        DisplayRow Row,
        string ProgressLine,
        PresentedPrompt Prompt,
        PresentedPrompt WaitingPrompt,
        ResultPresentation Passed,
        ResultPresentation Failed,
        IReadOnlyList<string> HandOverLines);

    internal void ShowPictureView(PictureView view, PictureContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Text = "Earshot live tests";
        _stepPanel.Visible = false;
        _resultPanel.Visible = false;
        _handOffBox.Visible = false;
        _runAllProgressLabel.Visible = false;
        _runAllStatusLabel.Text = string.Empty;
        _stopButton.Visible = false;
        _statusLabel.Text = string.Empty;

        switch (view)
        {
            case PictureView.Home:
                SwitchToView(MainView.Home);
                break;
            case PictureView.List:
                SwitchToView(MainView.List);
                SelectRow(content.Row);
                break;
            case PictureView.RunAll:
                ShowStep(content.Row, content.Prompt, content.ProgressLine);
                _runAllProgressLabel.Text = content.ProgressLine;
                _runAllProgressLabel.Visible = true;
                _runAllStatusLabel.Text = Copy.RunAllExplanation;
                break;
            case PictureView.Step:
                ShowStep(content.Row, content.Prompt, content.ProgressLine);
                break;
            case PictureView.StepWaiting:
                ShowStep(content.Row, content.WaitingPrompt, content.ProgressLine);
                break;
            case PictureView.Pass:
                ShowResult(content.Row, content.Passed, RowStateKind.Passed);
                break;
            case PictureView.Fail:
                ShowResult(content.Row, content.Failed, RowStateKind.Failed);
                break;
            case PictureView.HandOver:
                _handOffBox.Text = string.Join(Environment.NewLine, content.HandOverLines);
                _handOffBox.Visible = true;
                UpdateBackToStartVisibility();
                SwitchToView(MainView.Running);
                _statusLabel.Text = Copy.PlainWaitingForPowerCycleStatus(PowerCycleRequirement.FullShutDown, content.Row.Name);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(view));
        }
    }

    private void ShowStep(DisplayRow row, PresentedPrompt prompt, string progressLine)
    {
        // A runner that was never started: the panel keeps it only to answer with, and a picture is never answered.
        using var runner = new ChildRunner("none", "none", "none", "none", "none", resume: false, variant: 0, offerUninstall: false, allowPlanB: false);
        _stepPanel.Show(runner, prompt, seq: 1, showTechnicalDetails: false, progressLine);
        _stepPanel.Visible = true;
        _stepPanel.EnableAnswersNow();
        _stopButton.Visible = true;
        _stopButton.Enabled = true;
        UpdateBackToStartVisibility();
        SwitchToView(MainView.Running);
        _statusLabel.Text = Copy.PlainRunningStatus(row.Name, isResume: false);
    }

    private void ShowResult(DisplayRow row, ResultPresentation result, RowStateKind kind)
    {
        var state = new DerivedRowState { Kind = kind };
        _resultPanel.Show(result, showTechnicalDetails: false, kind);
        _resultPanel.Visible = true;
        UpdateBackToStartVisibility();
        SwitchToView(MainView.Running);
        _statusLabel.Text = Copy.PlainFinishedStatus(row.Name, RowPresenter.PlainText(state));
    }

    // Shown where no one can see it: off screen, never activated, and only on a desktop the caller vouches for.
    internal void ShowOffScreenForPictures(Func<bool> isOwnersDesktop)
    {
        ArgumentNullException.ThrowIfNull(isOwnersDesktop);
        if (isOwnersDesktop())
        {
            throw new InvalidOperationException("Pictures are drawn on a private desktop only.");
        }

        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        ShowInTaskbar = false;
        Show();
    }
}
