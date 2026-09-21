using Earshot.TestWindow.Core;

namespace Earshot.TestWindow.Ui;

// Renders one ResultPresentation: the failure list, the script's own errors, and
// the leftAtRest line. Holds no decisions: ResultPresenter and Copy already made them.
internal sealed class ResultPanel : Panel
{
    private readonly Label _plainSummaryLabel;
    private readonly Label _plainLinesLabel;
    private readonly Label _technicalHeading;
    private readonly ListView _failureList;
    private readonly Label _selectedCaption;
    private readonly TextBox _selectedDetailBox;
    private readonly Label _errorsCaption;
    private readonly TextBox _errorsBox;
    private readonly Label _evidenceLabel;
    private readonly Label _atRestLabel;
    private readonly Label _declinedElevatedPromptLabel;
    private IReadOnlyList<FailureRow> _failures = Array.Empty<FailureRow>();

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
        _atRestLabel = new Label { AutoSize = true, MaximumSize = new Size(704, 0), Font = new Font(Font, FontStyle.Bold) };

        // Shown here rather than only in a criterion's own detail text, since "you chose No on
        // the Windows permission box" is the one fact about the run that most needs to be seen
        // without having to open the failure list first.
        _declinedElevatedPromptLabel = new Label
        {
            AutoSize = true, MaximumSize = new Size(704, 0), ForeColor = Color.DarkRed, Visible = false,
            Margin = new Padding(0, 0, 0, 8),
        };

        stack.Controls.Add(heading);
        stack.Controls.Add(_declinedElevatedPromptLabel);
        stack.Controls.Add(_plainSummaryLabel);
        stack.Controls.Add(_plainLinesLabel);
        stack.Controls.Add(_technicalHeading);
        stack.Controls.Add(_failureList);
        stack.Controls.Add(_selectedCaption);
        stack.Controls.Add(_selectedDetailBox);
        stack.Controls.Add(_errorsCaption);
        stack.Controls.Add(_errorsBox);
        stack.Controls.Add(_evidenceLabel);
        stack.Controls.Add(_atRestLabel);

        Controls.Add(stack);
    }

    // showTechnicalDetails gates the failure table (Criterion, Expected, Observed: the script's own
    // check ids and its own words for what it expected and observed), the selected-criterion detail
    // box and the raw errors box, the same script-authored content Copy.RestoreUninstallOfferNotAvailable-
    // style strings never are. The evidence path and the leftAtRest line are this window's own
    // words (Copy), never the script's, so neither is ever hidden.
    internal void Show(ResultPresentation result, bool showTechnicalDetails)
    {
        ArgumentNullException.ThrowIfNull(result);

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

        _evidenceLabel.Text = "The saved record of this run is here:" + Environment.NewLine +
            result.EvidenceFolder + Environment.NewLine + result.ResultJsonPath + Environment.NewLine + result.SummaryTxtPath;
        // A raw folder and file path is jargon (plain-window-layout.md's Result section replaces
        // this with a plain line and an "Open the folder" button in a later pass); until then, it
        // must simply never appear when technical details is off.
        _evidenceLabel.Visible = showTechnicalDetails;

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

    internal bool TechnicalDetailsVisibleForTests => _failureList.Visible;

    internal string EvidenceTextForTests => _evidenceLabel.Text;

    internal bool EvidenceVisibleForTests => _evidenceLabel.Visible;

    internal string AtRestTextForTests => _atRestLabel.Text;

    internal bool PlainSummaryVisibleForTests => _plainSummaryLabel.Visible;

    internal string PlainSummaryTextForTests => _plainSummaryLabel.Text;

    internal string PlainLinesTextForTests => _plainLinesLabel.Text;
}
