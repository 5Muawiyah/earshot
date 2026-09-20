using Earshot.TestWindow.Core;

namespace Earshot.TestWindow.Ui;

// Renders one ResultPresentation (section 12): the failure list, the script's own errors, and
// the leftAtRest line. Holds no decisions: ResultPresenter and Copy already made them.
internal sealed class ResultPanel : Panel
{
    private readonly ListView _failureList;
    private readonly TextBox _errorsBox;
    private readonly Label _evidenceLabel;
    private readonly Label _atRestLabel;

    internal ResultPanel()
    {
        Dock = DockStyle.Fill;
        AutoScroll = true;

        // A top-down flow, so each control's real rendered height (a wrapped evidence path can
        // be several lines) always pushes the next one down instead of a fixed offset guessing
        // wrong and hiding the leftAtRest line underneath (design.md section 4/12: it must be
        // shown after every run).
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

        _failureList = new ListView
        {
            Width = 704, Height = 160, View = View.Details, FullRowSelect = true, GridLines = true, Margin = new Padding(0, 0, 0, 8),
        };
        _failureList.Columns.Add("Criterion", 140);
        _failureList.Columns.Add("Expected", 220);
        _failureList.Columns.Add("Observed", 320);

        var errorsCaption = new Label { Text = "Errors the test recorded", AutoSize = true };
        _errorsBox = new TextBox
        {
            Width = 704, Height = 60, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Margin = new Padding(0, 0, 0, 8),
        };

        _evidenceLabel = new Label { AutoSize = true, MaximumSize = new Size(704, 0), Margin = new Padding(0, 0, 0, 12) };
        _atRestLabel = new Label { AutoSize = true, MaximumSize = new Size(704, 0), Font = new Font(Font, FontStyle.Bold) };

        stack.Controls.Add(heading);
        stack.Controls.Add(_failureList);
        stack.Controls.Add(errorsCaption);
        stack.Controls.Add(_errorsBox);
        stack.Controls.Add(_evidenceLabel);
        stack.Controls.Add(_atRestLabel);

        Controls.Add(stack);
    }

    internal void Show(ResultPresentation result)
    {
        ArgumentNullException.ThrowIfNull(result);

        _failureList.Items.Clear();
        foreach (FailureRow failure in result.Failures)
        {
            var item = new ListViewItem(failure.Id);
            item.SubItems.Add(failure.Expected);
            item.SubItems.Add(failure.Observed);
            _failureList.Items.Add(item);
        }

        _errorsBox.Text = string.Join(Environment.NewLine, result.Errors);
        _evidenceLabel.Text = "Evidence: " + result.EvidenceFolder + Environment.NewLine +
            result.ResultJsonPath + Environment.NewLine + result.SummaryTxtPath;

        string atRestText = Copy.LeftAtRestText(result.LeftAtRest, result.LeftAtRestDetail);
        _atRestLabel.Text = atRestText;
        _atRestLabel.ForeColor = result.LeftAtRest is "yes" or "not-applicable" ? Color.DarkGreen : Color.DarkRed;
    }
}
