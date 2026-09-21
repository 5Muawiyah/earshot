using Earshot.TestWindow.Core;

namespace Earshot.TestWindow.Ui;

// Renders one ResultPresentation: the failure list, the script's own errors, and
// the leftAtRest line. Holds no decisions: ResultPresenter and Copy already made them.
internal sealed class ResultPanel : Panel
{
    private readonly Label _verdictLabel;
    private readonly Label _plainSummaryLabel;
    private readonly Label _plainLinesLabel;
    private readonly Label _technicalHeading;
    private readonly ListView _failureList;
    private readonly Label _selectedCaption;
    private readonly TextBox _selectedDetailBox;
    private readonly Label _errorsCaption;
    private readonly TextBox _errorsBox;
    private readonly Label _evidenceLabel;
    private readonly Label _plainRecordSavedLabel;
    private readonly Button _openFolderButton;
    private readonly Label _atRestLabel;
    private readonly Label _declinedElevatedPromptLabel;
    private IReadOnlyList<FailureRow> _failures = Array.Empty<FailureRow>();
    private string? _currentEvidenceFolder;

    internal ResultPanel()
    {
        Dock = DockStyle.Fill;
        AutoScroll = true;

        // A top-down flow, so each control's real rendered height (a wrapped evidence path can
        // be several lines) always pushes the next one down instead of a fixed offset guessing
        // wrong and hiding the leftAtRest line underneath: it must be shown after every run.
        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Top = 8,
            Left = 8,
            Width = 720,
        };

        var heading = new Label { Text = "Result", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 8) };

        // The Result view's own single verdict line (plain-window-layout.md): one large sentence
        // with a symbol, never gated by the technical-details toggle (this window's own words,
        // never the script's, the same reasoning _atRestLabel below already follows).
        _verdictLabel = new Label
        {
            AutoSize = true, MaximumSize = new Size(704, 0), Font = new Font(Font.FontFamily, 16f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 10),
        };

        // The plain-mode summary: shown with technical details off, in place of the technical
        // table below (which stays exactly as it was, behind the toggle).
        _plainSummaryLabel = new Label
        {
            AutoSize = true, MaximumSize = new Size(704, 0), Font = new Font(Font.FontFamily, 11f), Margin = new Padding(0, 0, 0, 4),
        };
        _plainLinesLabel = new Label { AutoSize = true, MaximumSize = new Size(704, 0), Margin = new Padding(0, 0, 0, 8) };

        _technicalHeading = new Label
        {
            Text = "Technical details", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 4, 0, 2),
        };

        _failureList = new ListView
        {
            Width = 704, Height = 160, View = View.Details, FullRowSelect = true, GridLines = true, Margin = new Padding(0, 0, 0, 4),
        };
        _failureList.Columns.Add("Criterion", 140);
        _failureList.Columns.Add("Expected", 220);
        _failureList.Columns.Add("Observed", 320);
        _failureList.SelectedIndexChanged += (_, _) => ShowSelectedFailureInFull();

        // The table truncates long text with an ellipsis; the row picked (the first one, by
        // default, so nothing is hidden until a click) is always shown here in full, never cut.
        _selectedCaption = new Label { Text = "Selected criterion, in full", AutoSize = true, Margin = new Padding(0, 0, 0, 2) };
        _selectedDetailBox = new TextBox
        {
            Width = 704, Height = 70, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            WordWrap = true, Margin = new Padding(0, 0, 0, 8),
        };

        _errorsCaption = new Label { Text = "Errors the test recorded", AutoSize = true };
        _errorsBox = new TextBox
        {
            Width = 704, Height = 60, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Margin = new Padding(0, 0, 0, 8),
        };

        _evidenceLabel = new Label { AutoSize = true, MaximumSize = new Size(704, 0), Margin = new Padding(0, 0, 0, 12) };

        // Plain mode's own replacement for the raw paths above (plain-window-layout.md's Result
        // section: "File paths are jargon: behind technical details. In plain mode one line ... and
        // a button"): the line and the button that actually opens the saved evidence folder for the
        // owner, both gated the same way the raw paths are, only the other way round.
        _plainRecordSavedLabel = new Label
        {
            AutoSize = true, MaximumSize = new Size(704, 0), Margin = new Padding(0, 0, 0, 4), Text = Copy.ResultRecordSavedLine,
        };
        _openFolderButton = new Button { Text = Copy.OpenFolderButtonLabel, AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
        _openFolderButton.Click += (_, _) => OnOpenFolderClicked();

        _atRestLabel = new Label { AutoSize = true, MaximumSize = new Size(704, 0), Font = new Font(Font, FontStyle.Bold) };

        // Shown here rather than only in a criterion's own detail text, since "you chose No on
        // the Windows permission box" is the one fact about the run that most needs to be seen
        // without having to open the failure list first.
        _declinedElevatedPromptLabel = new Label
        {
            AutoSize = true, MaximumSize = new Size(704, 0), ForeColor = Color.DarkRed, Visible = false,
            Margin = new Padding(0, 0, 0, 8),
        };

        // Spec order (plain-window-layout.md's Result section): verdict; the plain line per check
        // that did not work; then the left-at-rest line in plain words; then, jargon behind the
        // toggle either way, the raw paths (technical) or the saved-record line and its button
        // (plain).
        stack.Controls.Add(heading);
        stack.Controls.Add(_verdictLabel);
        stack.Controls.Add(_declinedElevatedPromptLabel);
        stack.Controls.Add(_plainSummaryLabel);
        stack.Controls.Add(_plainLinesLabel);
        stack.Controls.Add(_atRestLabel);
        stack.Controls.Add(_technicalHeading);
        stack.Controls.Add(_failureList);
        stack.Controls.Add(_selectedCaption);
        stack.Controls.Add(_selectedDetailBox);
        stack.Controls.Add(_errorsCaption);
        stack.Controls.Add(_errorsBox);
        stack.Controls.Add(_evidenceLabel);
        stack.Controls.Add(_plainRecordSavedLabel);
        stack.Controls.Add(_openFolderButton);

        Controls.Add(stack);
    }

    // showTechnicalDetails gates the failure table (Criterion, Expected, Observed: the script's own
    // check ids and its own words for what it expected and observed), the selected-criterion detail
    // box and the raw errors box, the same script-authored content Copy.RestoreUninstallOfferNotAvailable-
    // style strings never are. The evidence path and the leftAtRest line are this window's own
    // words (Copy), never the script's, so neither is ever hidden.
    // verdictKind is the row's own DerivedRowState.Kind, sourced by the caller from the same single
    // place StateDeriver's result is already computed (MainForm.ComputeState) - never re-derived
    // here, so the row list and this headline can never disagree about pass/fail/inconclusive/
    // unknown.
    internal void Show(ResultPresentation result, bool showTechnicalDetails, RowStateKind verdictKind)
    {
        ArgumentNullException.ThrowIfNull(result);

        _verdictLabel.Text = Copy.ResultVerdictLine(verdictKind);
        _verdictLabel.ForeColor = verdictKind switch
        {
            RowStateKind.Passed => Color.DarkGreen,
            RowStateKind.Inconclusive => Color.Black,
            _ => Color.DarkRed,
        };

        _declinedElevatedPromptLabel.Visible = result.HasDeclinedElevatedStep;
        _declinedElevatedPromptLabel.Text = Copy.DeclinedElevatedPrompt;

        _failures = result.Failures;
        _failureList.Items.Clear();
        foreach (FailureRow failure in result.Failures)
        {
            var item = new ListViewItem(failure.Id);
            item.SubItems.Add(failure.Expected);
            item.SubItems.Add(failure.Observed);
            _failureList.Items.Add(item);
        }

        if (_failureList.Items.Count > 0)
        {
            // First failed first: the first row is shown in full without waiting
            // for a click, so nothing is hidden by the ellipsis by default.
            _failureList.Items[0].Selected = true;
        }
        else
        {
            _selectedDetailBox.Text = string.Empty;
        }

        _errorsBox.Text = string.Join(Environment.NewLine, result.Errors);

        _plainSummaryLabel.Text = Copy.PlainCheckSummarySentence(result.TotalCriteria, result.FailedCount, result.InconclusiveCount);
        _plainLinesLabel.Text = string.Join(
            Environment.NewLine + Environment.NewLine,
            result.PlainFailureLines.Select(line => line.PlainMeaning is null ? line.PlainLine : line.PlainLine + Environment.NewLine + line.PlainMeaning));
        _plainSummaryLabel.Visible = !showTechnicalDetails;
        _plainLinesLabel.Visible = !showTechnicalDetails && result.PlainFailureLines.Count > 0;

        _technicalHeading.Visible = showTechnicalDetails;
        _failureList.Visible = showTechnicalDetails;
        _selectedCaption.Visible = showTechnicalDetails;
        _selectedDetailBox.Visible = showTechnicalDetails;
        _errorsCaption.Visible = showTechnicalDetails;
        _errorsBox.Visible = showTechnicalDetails;

        _currentEvidenceFolder = result.EvidenceFolder;

        _evidenceLabel.Text = "The saved record of this run is here:" + Environment.NewLine +
            result.EvidenceFolder + Environment.NewLine + result.ResultJsonPath + Environment.NewLine + result.SummaryTxtPath;
        // A raw folder and file path is jargon: never shown when technical details is off, where
        // the plain-mode saved-record line and its "Open the folder" button (below) take its place.
        _evidenceLabel.Visible = showTechnicalDetails;
        _plainRecordSavedLabel.Visible = !showTechnicalDetails;
        _openFolderButton.Visible = !showTechnicalDetails;

        string atRestText = Copy.LeftAtRestText(result.LeftAtRest, result.LeftAtRestDetail);
        _atRestLabel.Text = atRestText;
        _atRestLabel.ForeColor = result.LeftAtRest is "yes" or "not-applicable" ? Color.DarkGreen : Color.DarkRed;
    }

    private void ShowSelectedFailureInFull()
    {
        if (_failureList.SelectedIndices.Count == 0)
        {
            _selectedDetailBox.Text = string.Empty;
            return;
        }

        int index = _failureList.SelectedIndices[0];
        if (index < 0 || index >= _failures.Count)
        {
            _selectedDetailBox.Text = string.Empty;
            return;
        }

        FailureRow failure = _failures[index];
        _selectedDetailBox.Text =
            "Criterion: " + failure.Id + Environment.NewLine +
            "Expected: " + failure.Expected + Environment.NewLine +
            "Observed: " + failure.Observed;
    }

    // The real "Open the folder" click handler: never Explorer itself in a test (OpenFolderForTests
    // is a test seam, the same shape as MainForm's own ChooseExePathDialogForTests/
    // ConfirmDialogForTests), defaulting to the one place this window is allowed to start a process
    // (ChildRunner.OpenFolderInExplorer; NoDevicePathTests.ProcessIsStartedOnlyFromChildRunner pins
    // that). Caught, not swallowed: a missing or moved evidence folder must never take the whole
    // window down over a convenience button, but the raw exception is still recorded, the same way
    // MainForm.SafeBeginInvoke already records what it catches.
    private void OnOpenFolderClicked()
    {
        if (_currentEvidenceFolder is null)
        {
            return;
        }

        try
        {
            OpenFolderForTests(_currentEvidenceFolder);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Open the folder failed for " + _currentEvidenceFolder + ": " + ex);
        }
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Action<string> OpenFolderForTests { get; set; } = ChildRunner.OpenFolderInExplorer;

    internal bool TechnicalDetailsVisibleForTests => _failureList.Visible;

    internal string EvidenceTextForTests => _evidenceLabel.Text;

    internal bool EvidenceVisibleForTests => _evidenceLabel.Visible;

    internal string AtRestTextForTests => _atRestLabel.Text;

    internal bool PlainSummaryVisibleForTests => _plainSummaryLabel.Visible;

    internal string PlainSummaryTextForTests => _plainSummaryLabel.Text;

    internal string PlainLinesTextForTests => _plainLinesLabel.Text;

    internal string VerdictTextForTests => _verdictLabel.Text;

    internal bool PlainRecordSavedVisibleForTests => _plainRecordSavedLabel.Visible;

    internal string PlainRecordSavedTextForTests => _plainRecordSavedLabel.Text;

    internal bool OpenFolderButtonVisibleForTests => _openFolderButton.Visible;

    internal void ClickOpenFolderButtonForTests() => _openFolderButton.PerformClick();
}
