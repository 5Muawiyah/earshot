using Earshot.TestWindow.Core;

namespace Earshot.TestWindow.Ui;

// Renders one PresentedPrompt and turns a button click into exactly one
// ChildRunner.ReplyFromOwnerClick call. Holds no decisions of its own:
// PromptPresenter decided what to show, this only draws it.
//
// Internal layout (plain-window-layout.md, "Step (fills the window while a test runs)"), top to
// bottom, in one FlowLayoutPanel whose child order Show() rearranges every time:
//   1. the progress line ("Test N of M <middle dot> <row name>"), when this run has a position in
//      Run all's own order;
//   2. the heading;
//   3. the checklist/instruction as large wrapped text (a Label now, never a ListBox: a ListBox
//      cannot wrap, so it used to clip a long line at its own fixed width);
//   4. the numbered how-to actions beside their picture (a FlowLayoutPanel row, the same left-text
//      right-picture shape MainForm's own banner how-to row already uses);
//   5. the question, directly above the answer buttons;
//   6. the answer buttons;
//   7. (Stop stays a MainForm-level control pinned under the whole Running view, never part of
//      this panel: see MainForm.cs's own runningPanel construction.)
// then, only with technical details on, a "Technical details" panel holding the script's own raw
// words, last.
//
// Which physical label plays "the checklist" (point 3) versus "the question" (point 5) depends on
// the prompt: Show-Preconditions' own plain line ("Are all of those true, and are you ready to
// start?") IS the question about the checklist above it, so it moves to point 5 rather than
// sitting above the checklist it asks about (the named bug this redesign fixes). Wait-Owner has no
// checklist; its plain line is the instruction (point 3) and "Have you done it?" is the question
// (point 5). Confirm-Step, Read-Answer and Read-Note have no checklist and no non-technical detail
// line of their own, so their plain line is the only text and sits at point 3, with nothing
// duplicated at point 5.
internal sealed class StepPanel : Panel
{
    // Buttons are disabled for 800 ms after a prompt appears, so a fast double
    // click on the previous prompt's position cannot answer this one.
    private static readonly TimeSpan ClickSafetyDelay = TimeSpan.FromMilliseconds(800);

    // "Plain line font at least 14 pt, body at least 11 pt" (plain-window-layout.md): PlainLine is
    // the short headline sentence, wherever it ends up (point 3 or point 5 above); the checklist
    // and the detail-derived question text are both "body" and use the smaller floor.
    private const float PlainLineFontSize = 14f;
    private const float BodyFontSize = 11f;
    private const float ButtonFontSize = 13f;
    private const int ButtonMinHeight = 48;
    private const int MinWrapWidth = 240;
    private const int HowToPictureSize = 160;

    private readonly FlowLayoutPanel _layout;

    private readonly Label _progressLabel;
    private readonly Label _headingLabel;
    private readonly Label _plainLineLabel;
    private readonly Label _checklistLabel;
    private readonly Label _questionLabel;

    private readonly FlowLayoutPanel _howToRow;
    private readonly Label _howToStepsLabel;
    private readonly PictureBox _howToPictureBox;

    private readonly FlowLayoutPanel _buttonRow;
    private readonly Label _acknowledgementLabel;

    private readonly FlowLayoutPanel _technicalPanel;
    private readonly Label _technicalHeadingLabel;
    private readonly Label _scriptWordsCaption;
    private readonly TextBox _scriptWordsBox;

    private readonly System.Windows.Forms.Timer _clickSafetyTimer;

    private ChildRunner? _runner;
    private int _currentSeq;
    private IReadOnlyList<string> _plainListItemsForTests = Array.Empty<string>();
    private Control? _bodyControlForTests;
    private Control? _questionControlForTests;

    // The one notification MainForm needs to clear its own silence-watchdog state
    // (_currentPromptSeq) the moment a reply actually leaves this panel, not only when the run
    // itself ends. Raised only for a reply that was actually sent (Abort does not raise it: the
    // watchdog's own kill-deadline countdown takes over from there).
    internal event Action<int>? ReplySent;

    internal StepPanel()
    {
        Dock = DockStyle.Fill;
        AutoScroll = true;

        _layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Padding = new Padding(8),
        };

        _progressLabel = new Label
        {
            AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 6), Visible = false,
        };
        _headingLabel = new Label { AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 6) };
        _plainLineLabel = new Label { AutoSize = true, Font = new Font(Font.FontFamily, PlainLineFontSize), Margin = new Padding(0, 0, 0, 10) };
        _checklistLabel = new Label
        {
            AutoSize = true, Font = new Font(Font.FontFamily, BodyFontSize), Margin = new Padding(0, 0, 0, 10), Visible = false,
        };
        _questionLabel = new Label
        {
            AutoSize = true, Font = new Font(Font.FontFamily, BodyFontSize, FontStyle.Bold), Margin = new Padding(0, 4, 0, 10), Visible = false,
        };

        _howToStepsLabel = new Label { AutoSize = true, Font = new Font(Font.FontFamily, BodyFontSize) };
        _howToPictureBox = new PictureBox
        {
            Width = HowToPictureSize, Height = HowToPictureSize, SizeMode = PictureBoxSizeMode.Zoom,
            BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(16, 0, 0, 0), Visible = false,
        };
        _howToRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 10), Visible = false,
        };
        _howToRow.Controls.Add(_howToStepsLabel);
        _howToRow.Controls.Add(_howToPictureBox);

        // "at least 44 px high, 13 pt, evenly spaced" (plain-window-layout.md): a fixed minimum
        // size holds even while AutoSize lets a longer label (a chosen speaker name, say) grow
        // wider; the same Margin on every button is what makes the spacing even.
        _buttonRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 6),
        };
        _acknowledgementLabel = new Label
        {
            AutoSize = true, Font = new Font(Font.FontFamily, BodyFontSize), ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 6),
        };

        _technicalHeadingLabel = new Label
        {
            AutoSize = true, Font = new Font(Font, FontStyle.Bold), Text = "Technical details", Visible = false,
        };
        _scriptWordsCaption = new Label
        {
            AutoSize = true, Text = "The test's own words", ForeColor = SystemColors.GrayText, Visible = false,
        };
        _scriptWordsBox = new TextBox
        {
            Width = 640, Height = 90, Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Control, ForeColor = SystemColors.GrayText,
            Visible = false,
        };
        _technicalPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 8, 0, 0),
        };
        _technicalPanel.Controls.Add(_technicalHeadingLabel);
        _technicalPanel.Controls.Add(_scriptWordsCaption);
        _technicalPanel.Controls.Add(_scriptWordsBox);

        _layout.Controls.Add(_progressLabel);
        _layout.Controls.Add(_headingLabel);
        _layout.Controls.Add(_checklistLabel);
        _layout.Controls.Add(_plainLineLabel);
        _layout.Controls.Add(_howToRow);
        _layout.Controls.Add(_questionLabel);
        _layout.Controls.Add(_buttonRow);
        _layout.Controls.Add(_acknowledgementLabel);
        _layout.Controls.Add(_technicalPanel);
        Controls.Add(_layout);

        _clickSafetyTimer = new System.Windows.Forms.Timer { Interval = (int)ClickSafetyDelay.TotalMilliseconds };
        _clickSafetyTimer.Tick += (_, _) =>
        {
            _clickSafetyTimer.Stop();
            foreach (Control control in _buttonRow.Controls)
            {
                control.Enabled = true;
            }
        };

        ApplyWrapWidths();
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
    //
    // progressText is "Test N of M <middle dot> <row name>" (Copy.StepProgressLine), already
    // formatted by MainForm (which is the one place that knows both this run's row and Run all's
    // own order): null or empty hides the progress line rather than showing a stray one for a row
    // outside that order (00 Restore, the administrator prompt check).
    internal void Show(ChildRunner runner, PresentedPrompt prompt, int seq, bool showTechnicalDetails, string? progressText = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(prompt);

        _runner = runner;
        _currentSeq = seq;
        _acknowledgementLabel.Text = string.Empty;

        _progressLabel.Text = progressText ?? string.Empty;
        _progressLabel.Visible = !string.IsNullOrEmpty(progressText);

        _headingLabel.Text = prompt.Heading;
        _plainLineLabel.Text = prompt.PlainLine;

        bool hasList = prompt.PlainListItems.Count > 0 || prompt.PlainPhysicalActions.Count > 0;
        if (hasList)
        {
            var lines = new List<string>();
            foreach (string item in prompt.PlainListItems)
            {
                lines.Add("- " + item);
            }

            foreach (string action in prompt.PlainPhysicalActions)
            {
                lines.Add("* " + action);
            }

            _plainListItemsForTests = lines;
            _checklistLabel.Text = string.Join(Environment.NewLine, lines);
            _checklistLabel.Visible = true;
        }
        else
        {
            _plainListItemsForTests = Array.Empty<string>();
            _checklistLabel.Text = string.Empty;
            _checklistLabel.Visible = false;
        }

        // Wait-Owner's "Have you done it?" is this window's own plain wording
        // (DetailIsTechnical is false for it): the one case with a real, separate question beneath
        // its own instruction. Confirm-Step's raw "What it does:" consequence is the script's own
        // words, so it stays behind the technical-details toggle instead (folded into
        // technicalLines below), never shown here.
        string? detailQuestion = prompt.DetailLabel is null || prompt.DetailIsTechnical
            ? null
            : prompt.DetailLabel + " " + prompt.DetailText;

        // Show-Preconditions' own plain line is the question about the checklist above it
        // ("Are all of those true, and are you ready to start?"): it belongs at point 5, directly
        // above the buttons, not at point 3 where a plain instruction would sit.
        bool plainLineIsQuestion = hasList;
        if (plainLineIsQuestion)
        {
            _questionLabel.Text = string.Empty;
            _questionLabel.Visible = false;
            _bodyControlForTests = _checklistLabel;
            _questionControlForTests = _plainLineLabel;
        }
        else if (detailQuestion is not null)
        {
            _questionLabel.Text = detailQuestion;
            _questionLabel.Visible = true;
            _bodyControlForTests = _plainLineLabel;
            _questionControlForTests = _questionLabel;
        }
        else
        {
            _questionLabel.Text = string.Empty;
            _questionLabel.Visible = false;
            _bodyControlForTests = _plainLineLabel;
            _questionControlForTests = null;
        }

        ArrangeOrder(plainLineIsQuestion);

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

        _scriptWordsBox.Text = string.Join(Environment.NewLine, technicalLines);
        bool hasTechnicalContent = technicalLines.Count > 0;
        bool showTechnical = showTechnicalDetails && hasTechnicalContent;
        _technicalHeadingLabel.Visible = showTechnical;
        _scriptWordsCaption.Visible = showTechnical;
        _scriptWordsBox.Visible = showTechnical;

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

        ApplyWrapWidths();

        // "No default button" (plain-window-layout.md): a probe against this window's own real
        // behaviour (MainFormTestHarness, a real sandboxed run) found that a real WinForms Button
        // never keeps real keyboard focus (Control.Focused) across a prompt refresh here even
        // without this call - Controls.Clear() already drops it. What the same probe found left
        // behind was the Form's own ActiveControl bookkeeping (ContainerControl's "which branch of
        // the tree last had focus" pointer) still aimed at the button row itself, sometimes for the
        // rest of the run: a stale pointer into the very controls "no default button" is about,
        // with nothing that put it there on purpose. Clearing it here, every time a new prompt's
        // buttons are built, means the next Tab (still fully reachable, still deliberate) starts
        // clean rather than resuming inside the answer row from state nobody chose.
        ClearStaleActiveControl();
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
            _howToRow.Visible = false;
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
        _howToRow.Visible = true;

        Image? picture = pictureName is null ? null : HowToPictures.TryLoad(pictureName);
        _howToPictureBox.Image = picture;
        _howToPictureBox.Visible = picture is not null;
    }

    // Sets _layout's child order to match the seven-point spec order for this prompt: every
    // control this panel owns appears exactly once, in the right slot, whether or not it is
    // currently Visible (an invisible child takes no space in a FlowLayoutPanel, so its exact
    // index among the others does not affect what is drawn, only keeps this method's own
    // accounting exhaustive and simple to check).
    private void ArrangeOrder(bool plainLineIsQuestion)
    {
        var order = new List<Control> { _progressLabel, _headingLabel };
        if (plainLineIsQuestion)
        {
            order.Add(_checklistLabel);
        }
        else
        {
            order.Add(_checklistLabel);
            order.Add(_plainLineLabel);
        }

        order.Add(_howToRow);
        if (plainLineIsQuestion)
        {
            order.Add(_plainLineLabel);
        }

        order.Add(_questionLabel);
        order.Add(_buttonRow);
        order.Add(_acknowledgementLabel);
        order.Add(_technicalPanel);

        for (int i = 0; i < order.Count; i++)
        {
            _layout.Controls.SetChildIndex(order[i], i);
        }
    }

    // Reflow on resize (plain-window-layout.md: "the window stays resizable and the layout
    // reflows"): every wrapping label's own MaximumSize.Width is recomputed from this panel's
    // current client width, not fixed once at construction, and the how-to row's own label is
    // narrowed further to leave room for its fixed-size picture so the row never needs to grow
    // wider than this panel to fit both.
    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        ApplyWrapWidths();
    }

    private void ApplyWrapWidths()
    {
        int available = Math.Max(MinWrapWidth, ClientSize.Width - _layout.Padding.Horizontal - 4);
        _progressLabel.MaximumSize = new Size(available, 0);
        _headingLabel.MaximumSize = new Size(available, 0);
        _plainLineLabel.MaximumSize = new Size(available, 0);
        _checklistLabel.MaximumSize = new Size(available, 0);
        _questionLabel.MaximumSize = new Size(available, 0);
        _acknowledgementLabel.MaximumSize = new Size(available, 0);

        int howToAvailable = Math.Max(MinWrapWidth, available - HowToPictureSize - _howToPictureBox.Margin.Horizontal);
        _howToStepsLabel.MaximumSize = new Size(howToAvailable, 0);
    }

    // "Enter and Space never answer by accident: no default button." Form.AcceptButton is never
    // set anywhere in MainForm (so the one unconditional WinForms mechanism for turning Enter into
    // a click, regardless of focus, never applies here). What this clears is the other, quieter
    // one: Form.ActiveControl, which a real run leaves pointing at this row's own FlowLayoutPanel
    // once the row is built, with nothing here ever having asked for that. Clearing it after every
    // rebuild means a fresh prompt never inherits focus intent aimed at the previous one's buttons.
    private void ClearStaleActiveControl()
    {
        Form? form = FindForm();
        if (form is null)
        {
            return;
        }

        if (ReferenceEquals(form.ActiveControl, _buttonRow) || (form.ActiveControl is Control active && _buttonRow.Controls.Contains(active)))
        {
            form.ActiveControl = null;
        }
    }

    private Button BuildButton(PromptButton button)
    {
        var control = new Button
        {
            Text = button.Label, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, ButtonMinHeight), Font = new Font(Font.FontFamily, ButtonFontSize),
            Padding = new Padding(12, 0, 12, 0), Margin = new Padding(6),
        };
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
        var control = new Button
        {
            Text = "Stop the test", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, ButtonMinHeight), Font = new Font(Font.FontFamily, ButtonFontSize),
            Padding = new Padding(12, 0, 12, 0), Margin = new Padding(6),
        };
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

    internal string HeadingTextForTests => _headingLabel.Text;

    // Always prompt.PlainLine's own raw text, whichever slot it is currently laid out in (the
    // checklist's own question for Show-Preconditions, or the instruction body for every other
    // prompt): callers that only care about the words (NoBannedWordsInPlainModeTests) never need
    // to know which.
    internal string PlainLineTextForTests => _plainLineLabel.Text;

    internal IReadOnlyList<string> ListItemsForTests => _plainListItemsForTests;

    internal bool TechnicalDetailsVisibleForTests => _scriptWordsBox.Visible;

    internal string TechnicalDetailsTextForTests => _scriptWordsBox.Text;

    internal bool HowToStepsVisibleForTests => _howToStepsLabel.Visible;

    internal string HowToStepsTextForTests => _howToStepsLabel.Text;

    internal bool HowToPictureVisibleForTests => _howToPictureBox.Visible;

    internal bool HowToPictureLoadedForTests => _howToPictureBox.Image is not null;

    internal Control HowToPictureControlForTests => _howToPictureBox;

    internal bool ProgressVisibleForTests => _progressLabel.Visible;

    internal string ProgressTextForTests => _progressLabel.Text;

    // Whichever control currently holds the checklist/instruction text (point 3): the wrapping
    // Label that replaced the old clipping ListBox.
    internal Control BodyControlForTests => _bodyControlForTests!;

    // Whichever control currently holds the question text directly above the buttons (point 5),
    // or null for a prompt with nothing separate to ask there (Confirm-Step, Read-Answer,
    // Read-Note: the body text at point 3 is already the only thing to answer).
    internal Control? QuestionControlForTests => _questionControlForTests;

    internal Control ButtonRowForTests => _buttonRow;

    internal IReadOnlyList<Button> AnswerButtonsForTests => _buttonRow.Controls.OfType<Button>().ToList();

    internal float PlainLineFontSizeForTests => _plainLineLabel.Font.SizeInPoints;

    internal float ChecklistFontSizeForTests => _checklistLabel.Font.SizeInPoints;

    internal bool AnyAnswerButtonFocusedForTests => _buttonRow.Controls.Cast<Control>().Any(c => c.Focused);

    internal void FocusFirstAnswerButtonForTests() => (_buttonRow.Controls.Count > 0 ? _buttonRow.Controls[0] : null)?.Focus();
}
