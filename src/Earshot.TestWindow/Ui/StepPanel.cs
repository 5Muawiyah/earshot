using Earshot.TestWindow.Core;

namespace Earshot.TestWindow.Ui;

// Renders one PresentedPrompt and turns a button click into exactly one
// ChildRunner.ReplyFromOwnerClick call. Holds no decisions of its own:
// PromptPresenter decided what to show, this only draws it.
internal sealed class StepPanel : Panel
{
    // Buttons are disabled for 800 ms after a prompt appears, so a fast double
    // click on the previous prompt's position cannot answer this one.
    private static readonly TimeSpan ClickSafetyDelay = TimeSpan.FromMilliseconds(800);

    private readonly Label _headingLabel;
    private readonly Label _plainLineLabel;
    private readonly Label _detailLabel;
    private readonly ListBox _listItemsBox;
    private readonly Label _scriptWordsCaption;
    private readonly TextBox _scriptWordsBox;
    private readonly FlowLayoutPanel _buttonRow;
    private readonly Label _acknowledgementLabel;

    // The how-to block area: text left, picture right, below the buttons (which stay exactly where
    // they were), wrapping rather than growing sideways, so the panel never needs a horizontal
    // scroll bar at the window's default size.
    private readonly Label _howToStepsLabel;
    private readonly PictureBox _howToPictureBox;

    private readonly System.Windows.Forms.Timer _clickSafetyTimer;

    private ChildRunner? _runner;
    private int _currentSeq;

    // The one notification MainForm needs to clear its own silence-watchdog state
    // (_currentPromptSeq) the moment a reply actually leaves this panel, not only when the run
    // itself ends. Raised only for a reply that was actually sent (Abort does not raise it: the
    // watchdog's own kill-deadline countdown takes over from there).
    internal event Action<int>? ReplySent;

    internal StepPanel()
    {
        Dock = DockStyle.Fill;
        AutoScroll = true;

        _headingLabel = new Label { AutoSize = true, Font = new Font(Font, FontStyle.Bold), Top = 8, Left = 8 };
        _plainLineLabel = new Label { AutoSize = true, MaximumSize = new Size(560, 0), Top = 32, Left = 8, Font = new Font(Font.FontFamily, 11f) };
        _detailLabel = new Label { AutoSize = true, MaximumSize = new Size(560, 0), Top = 80, Left = 8 };
        _listItemsBox = new ListBox { Top = 80, Left = 8, Width = 560, Height = 80, Visible = false };
        _scriptWordsCaption = new Label { AutoSize = true, Text = "The test's own words", Top = 170, Left = 8, ForeColor = SystemColors.GrayText };
        _scriptWordsBox = new TextBox
        {
            Top = 190, Left = 8, Width = 560, Height = 50, Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Control, ForeColor = SystemColors.GrayText,
        };
        _buttonRow = new FlowLayoutPanel { Top = 250, Left = 8, Width = 560, Height = 40, FlowDirection = FlowDirection.LeftToRight };
        _acknowledgementLabel = new Label { AutoSize = true, MaximumSize = new Size(560, 0), Top = 296, Left = 8, ForeColor = SystemColors.GrayText };

        _howToStepsLabel = new Label { AutoSize = true, MaximumSize = new Size(360, 0), Top = 330, Left = 8, Visible = false };
        _howToPictureBox = new PictureBox
        {
            Top = 330, Left = 380, Width = 160, Height = 160, SizeMode = PictureBoxSizeMode.Zoom,
            BorderStyle = BorderStyle.FixedSingle, Visible = false,
        };

        _clickSafetyTimer = new System.Windows.Forms.Timer { Interval = (int)ClickSafetyDelay.TotalMilliseconds };
        _clickSafetyTimer.Tick += (_, _) =>
        {
            _clickSafetyTimer.Stop();
            foreach (Control control in _buttonRow.Controls)
            {
                control.Enabled = true;
            }
        };

        Controls.Add(_headingLabel);
        Controls.Add(_plainLineLabel);
        Controls.Add(_detailLabel);
        Controls.Add(_listItemsBox);
        Controls.Add(_scriptWordsCaption);
        Controls.Add(_scriptWordsBox);
        Controls.Add(_buttonRow);
        Controls.Add(_acknowledgementLabel);
        Controls.Add(_howToStepsLabel);
        Controls.Add(_howToPictureBox);
    }

    // Every button built here calls this, and nothing else in the form does (ChildRunner itself
    // is the single call site for the underlying reply; StepPanel is the single place a click
    // becomes that call).
    //
    // showTechnicalDetails gates every script-authored string this panel can show: the raw prompt
    // text (ScriptOwnWords), the raw preconditions and physical actions (ListItems,
    // PhysicalActions), and a Confirm-Step's raw consequence (DetailText, only when
    // prompt.DetailIsTechnical). Off, only PromptPresenter's own plain lines are visible: PlainLine
    // and, for Show-Preconditions, PlainListItems/PlainPhysicalActions. On, everything this panel
    // showed before this toggle existed is still shown, gathered under one "Technical details"
    // heading instead of scattered across the caption and the list box.
    internal void Show(ChildRunner runner, PresentedPrompt prompt, int seq, bool showTechnicalDetails)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(prompt);

        _runner = runner;
        _currentSeq = seq;
        _acknowledgementLabel.Text = string.Empty;

        _headingLabel.Text = prompt.Heading;
        _plainLineLabel.Text = prompt.PlainLine;

        if (prompt.PlainListItems.Count > 0 || prompt.PlainPhysicalActions.Count > 0)
        {
            _listItemsBox.Visible = true;
            _listItemsBox.Items.Clear();
            foreach (string item in prompt.PlainListItems)
            {
                _listItemsBox.Items.Add("- " + item);
            }

            foreach (string action in prompt.PlainPhysicalActions)
            {
                _listItemsBox.Items.Add("* " + action);
            }

            _detailLabel.Text = string.Empty;
        }
        else
        {
            _listItemsBox.Visible = false;

            // Wait-Owner's "Have you done it?" is this window's own plain wording
            // (DetailIsTechnical is false for it), always shown; Confirm-Step's raw "What it does:"
            // consequence is the script's own words, so it is folded into the technical block below
            // instead.
            _detailLabel.Text = prompt.DetailLabel is null || prompt.DetailIsTechnical
                ? string.Empty
                : prompt.DetailLabel + " " + prompt.DetailText;
        }

        var technicalLines = new List<string>();
        if (!string.IsNullOrEmpty(prompt.ScriptOwnWords))
        {
            technicalLines.Add(prompt.ScriptOwnWords);
        }

        foreach (string item in prompt.ListItems)
        {
            technicalLines.Add("- " + item);
        }

        foreach (string action in prompt.PhysicalActions)
        {
            technicalLines.Add("* " + action);
        }

        if (prompt.DetailIsTechnical && prompt.DetailLabel is not null)
        {
            technicalLines.Add(prompt.DetailLabel + " " + prompt.DetailText);
        }

        _scriptWordsCaption.Text = "Technical details";
        _scriptWordsBox.Text = string.Join(Environment.NewLine, technicalLines);
        bool hasTechnicalContent = technicalLines.Count > 0;
        _scriptWordsCaption.Visible = showTechnicalDetails && hasTechnicalContent;
        _scriptWordsBox.Visible = showTechnicalDetails && hasTechnicalContent;

        RenderHowTo(prompt.HowToBlocks);

        _buttonRow.Controls.Clear();
        if (prompt.StopOnly)
        {
            _buttonRow.Controls.Add(BuildStopButton());
        }
        else
        {
            foreach (PromptButton button in prompt.Buttons)
            {
                _buttonRow.Controls.Add(BuildButton(button));
            }
        }

        foreach (Control control in _buttonRow.Controls)
        {
            control.Enabled = false;
        }

        _clickSafetyTimer.Stop();
        _clickSafetyTimer.Start();
    }

    // Every named block's own steps, numbered in one continuous sequence across all of them (an
    // entry rarely names more than one), with the first block that actually has a picture shown
    // beside them; never hidden by the technical-details toggle, since these are this window's own
    // plain words, never the script's. A missing picture file shows nothing, never an error on
    // screen (HowToPictureCoverageTests is what catches that, for the writer to see).
    private void RenderHowTo(IReadOnlyList<HowToBlock> blocks)
    {
        _howToPictureBox.Image?.Dispose();
        _howToPictureBox.Image = null;

        if (blocks.Count == 0)
        {
            _howToStepsLabel.Visible = false;
            _howToPictureBox.Visible = false;
            return;
        }

        var lines = new List<string>();
        string? pictureName = null;
        int stepNumber = 1;
        foreach (HowToBlock block in blocks)
        {
            foreach (string step in block.Steps)
            {
                lines.Add(stepNumber + ". " + step);
                stepNumber++;
            }

            pictureName ??= block.Picture;
        }

        _howToStepsLabel.Text = string.Join(Environment.NewLine, lines);
        _howToStepsLabel.Visible = true;

        Image? picture = pictureName is null ? null : HowToPictures.TryLoad(pictureName);
        _howToPictureBox.Image = picture;
        _howToPictureBox.Visible = picture is not null;
    }

    private Button BuildButton(PromptButton button)
    {
        var control = new Button { Text = button.Label, AutoSize = true, Margin = new Padding(4) };
        int seqAtBuildTime = _currentSeq;
        control.Click += (_, _) =>
        {
            if (_currentSeq != seqAtBuildTime)
            {
                return;
            }

            if (button.SendsReply)
            {
                _runner?.ReplyFromOwnerClick(seqAtBuildTime, button.Reply);
                ReplySent?.Invoke(seqAtBuildTime);
            }
            else
            {
                // Wait-Owner's own "No": nothing is sent (the script is still waiting on the
                // same Read-Host, unanswered), so the screen has to say that plainly rather than
                // sit unchanged, which reads like the click did nothing at all.
                _acknowledgementLabel.Text = "Take your time. Click Yes when you have done it.";
            }
        };
        return control;
    }

    private Button BuildStopButton()
    {
        var control = new Button { Text = "Stop the test", AutoSize = true, Margin = new Padding(4) };
        int seqAtBuildTime = _currentSeq;
        control.Click += (_, _) => _runner?.Abort(seqAtBuildTime);
        return control;
    }

    // Test seam only: the same PerformClick pattern MainForm's own *ForTests members use.
    // PerformClick does nothing while a button is disabled (Button.CanSelect is false while
    // Enabled is false), the same as a real mouse click would find, so a test using this while the
    // click-safety timer still has the row's buttons disabled reaches nothing at all; callers wait
    // out ClickSafetyDelay first.
    internal void ClickFirstButtonForTests() => ClickButtonForTests(0);

    internal void ClickButtonForTests(int index) => (_buttonRow.Controls.Count > index ? _buttonRow.Controls[index] as Button : null)?.PerformClick();

    internal string AcknowledgementTextForTests => _acknowledgementLabel.Text;

    internal string PlainLineTextForTests => _plainLineLabel.Text;

    internal IReadOnlyList<string> ListItemsForTests => _listItemsBox.Items.Cast<string>().ToList();

    internal bool TechnicalDetailsVisibleForTests => _scriptWordsBox.Visible;

    internal string TechnicalDetailsTextForTests => _scriptWordsBox.Text;

    internal bool HowToStepsVisibleForTests => _howToStepsLabel.Visible;

    internal string HowToStepsTextForTests => _howToStepsLabel.Text;

    internal bool HowToPictureVisibleForTests => _howToPictureBox.Visible;

    internal bool HowToPictureLoadedForTests => _howToPictureBox.Image is not null;
}
