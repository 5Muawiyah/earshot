using Earshot.TestWindow.Core;

namespace Earshot.TestWindow.Ui;

// The window's shell. A row is picked from the list, Start begins a real (or, with --sandbox,
// fake-device) child, StepPanel shows every prompt, ResultPanel shows what result.json says
// once it exits. Test 10 is flattened into its five variant rows (DisplayRow.Flatten), so every
// one of the 16 tests and all 22 halves is its own clickable entry; none is a text box.
//
// Three views share this window's client area, exactly one Visible at a time (the layout's
// second pass): Home (the front page, and the default), List ("Choose one test") and Running (today's
// StepPanel/ResultPanel/hand-off box, unredesigned this commit; a later commit rebuilds its own inside
// without touching this switching mechanism). No logic moved: StateDeriver, Banner, RunGate,
// RunAllAdvance, the single start gate and every lock behave exactly as before; this is presentation.
// WinForms' own Control.Visible getter already folds a hidden parent into every child's own Visible
// read, so a control simply re-parented into a view panel that is not showing becomes unreachable by
// a real click (Button.PerformClick is a no-op on it) without anything here needing to track that
// separately for production code; only test seams that click such a control need to ask for the
// right view first (SwitchToView, and the small Ensure/Show*ForTests helpers below that call it).
internal sealed class MainForm : Form
{
    internal enum MainView { Home, List, Running }

    internal const int MinimumWindowWidth = 1000;
    internal const int MinimumWindowHeight = 680;
    private const int PreferredWindowWidth = 1150;
    private const int PreferredWindowHeight = 800;

    private readonly string _repoRoot;
    private readonly IReadOnlyList<ManifestRow> _rows;
    private readonly IReadOnlyList<DisplayRow> _displayRows;
    private readonly IReadOnlyList<WordingEntry> _wording;
    private readonly SandboxOptions? _sandbox;
    // The exe path the constructor was given is only the fallback now: ChooseExePath overwrites
    // this the moment a valid choice is made, and the constructor itself already prefers whatever
    // ExePathSettings has remembered from an earlier open.
    private string _exePath;
    private readonly Label _exePathLabel;
    private readonly Button _chooseExeButton;

    private readonly ListView _rowList;
    private readonly Label _rowDetailLabel;

    // Test 14 only: the second device's address is chosen from these buttons, never typed
    // (SpeakerCandidateFinder reads the candidates off an earlier run's own node evidence).
    // _chosenSpeakerAddress is null until one is clicked, or after "No second device" clears it,
    // and is what StartFreshRun forwards as -SpeakerAddress.
    private readonly Label _speakerChoiceLabel;
    private readonly FlowLayoutPanel _speakerChoiceRow;
    private string? _chosenSpeakerAddress;

    // A noted start (PowerCycleGateResult.StartNoted): shown instead of starting anything, until
    // this button is clicked. _pendingNotedStart itself is declared beside ShowNotedStartWarning.
    private readonly Label _notedStartWarningLabel;
    private readonly Button _notedStartButton;

    private readonly Button _startButton;
    private readonly Button _stopButton;
    private readonly ComboBox _caseBox;
    private readonly Label _statusLabel;
    private readonly Label _bannerLabel;
    private readonly StepPanel _stepPanel;
    private readonly ResultPanel _resultPanel;
    private readonly TextBox _handOffBox;
    private readonly Button _runAllButton;
    private readonly Label _runAllExplanationLabel;
    private readonly Label _runAllStatusLabel;
    private readonly Label _runAllProgressLabel;
    private readonly Button _runAllCarryOnButton;
    private readonly Button _runAllStopHereButton;

    // Result view navigation outside Run all (the layout's Result section: "Buttons:
    // in Run all, 'Carry on with the rest' and 'Stop here'; otherwise 'Back to the start'"). Shown
    // exactly when the Result view is on screen and Run all's own two buttons are not
    // (UpdateBackToStartVisibility); "the start" is Home, not the List view ("Choose one test"),
    // since this follows a single test's own result, distinct from List's own "Back".
    private readonly Button _backToStartButton;

    // Run all under a red or unknown banner: shown instead of starting anything, until the owner
    // has clicked through it (the same "have you done it" shape as a Wait-Owner step); only then is
    // row 00 Restore actually started, through the same single start gate every other route uses.
    private readonly Label _runAllRestoreAdviceLabel;
    private readonly Button _runAllRestoreContinueButton;
    private bool _runAllRecoveringViaRestore;
    private readonly System.Windows.Forms.Timer _watchdogTimer;

    // The administrator prompt check's own control, separate from the row list (it is a
    // utility check, not one of the 16 numbered tests, and never appears in Run all's own order).
    private readonly Button _rehearsalButton;
    private readonly Label _rehearsalWarningLabel;
    private readonly Label _rehearsalStatusLabel;
    private readonly DisplayRow _rehearsalRow;

    // Written from whichever ChildRunner's own background ReadLoop thread delivered the message
    // that raced this form's disposal, and from this UI thread too (KillActiveRun's own drain);
    // locked so two of those at once can never corrupt the list.
    private readonly object _postDisposalDeliveryFailuresGate = new();
    private readonly List<Exception> _postDisposalDeliveryFailures = new();

    private ChildRunner? _activeRunner;
    private string? _activeResultFolder;
    private bool _activeIsResume;
    private TestRowSpec? _activeSpec;
    private DisplayRow? _activeDisplayRow;
    private BannerState _banner = new() { Level = BannerLevel.None };
    private readonly List<string> _transcript = new();

    // "Show technical details", off by default and remembered beside the window's other settings
    // (TechnicalDetailsSettings, in WindowStateRoot). Off, no script-authored string is shown
    // anywhere: the step panel, the row detail, the result panel. On, everything this window used
    // to show unconditionally is still shown, labelled "Technical details". The last prompt and
    // result presented are cached so flipping the checkbox mid-step or mid-result redraws the
    // panel that is actually on screen immediately, rather than waiting for the next one.
    private readonly CheckBox _technicalDetailsCheckBox;
    private bool _showTechnicalDetails;
    private PresentedPrompt? _lastPresentedPrompt;
    private int _lastPresentedPromptSeq;
    private string? _lastPresentedPromptProgressText;
    private ResultPresentation? _lastResultPresentation;
    // Cached alongside _lastResultPresentation, only ever read while that is not null: the row's
    // own verdict kind at the moment the Result view was last shown, so flipping the
    // technical-details toggle can redraw the same panel without re-deriving anything.
    private RowStateKind _lastResultRowStateKind;
    // Cached the same way: a qualified pass ("shut down not confirmed", "on an earlier build", and
    // the rest) must read amber on the Result view's own verdict line too, not only on the row,
    // so this is never re-derived separately from the single ComputeState call that already found
    // it.
    private string? _lastResultQualifier;

    // The silence watchdog and the abort/kill sequence. A prompt on screen is never
    // a hang (test 12 waits hours at one), so the watchdog only ever looks at silence while
    // _currentPromptSeq is null. _killDeadlineUtc is set once, either by an abort waiting for a
    // natural exit or (implicitly, by going straight to the confirmation) when Stop is clicked
    // with nothing pending.
    private int? _currentPromptSeq;
    private DateTimeOffset _lastActivityUtc;
    private DateTimeOffset? _killDeadlineUtc;
    private bool _silenceWarningShown;

    // Run all's own state, held only in memory plus run-all.json; never consulted by
    // StateDeriver, so a row's own state is always exactly what the fail-closed evidence rules say
    // regardless of whether Run all is active.
    private bool _runAllActive;
    private int _runAllIndex = -1;
    private readonly Dictionary<string, string> _runAllPointers = new(StringComparer.Ordinal);

    // The three views (see the class remark above) and Home's own new controls.
    private MainView _currentView = MainView.Home;
    private readonly Panel _homePanel;
    private readonly Panel _listPanel;
    private readonly Panel _runningPanel;

    private readonly Label _homeCountLineLabel;
    private readonly Label _homePickupLineLabel;
    private readonly Button _chooseOneTestLinkButton;
    private readonly Button _moreToggleButton;
    private readonly FlowLayoutPanel _morePanel;
    private bool _moreOpen;
    private readonly Label _rehearsalTeaserLabel;
    private readonly Label _caseBoxLabel;
    private readonly Label _bannerHowToStepsLabel;
    private readonly PictureBox _bannerHowToPictureBox;
    private readonly Button _backButton;

    internal MainForm(string repoRoot, IReadOnlyList<ManifestRow> rows, IReadOnlyList<WordingEntry> wording, SandboxOptions? sandbox, string exePath)
    {
        _repoRoot = repoRoot;
        _rows = rows;
        _displayRows = DisplayRow.Flatten(rows);
        _wording = wording;
        _sandbox = sandbox;
        _exePath = exePath;

        Text = "Earshot live tests" + (sandbox is not null ? " (SANDBOX, no device)" : string.Empty);

        // A remembered, still-valid choice always wins over the fixed %ProgramFiles% fallback the
        // caller was constructed with; read before anything else here uses _exePath.
        _exePath = ExePathSettings.TryReadOrDefault(WindowStateRoot(), _exePath);
        _showTechnicalDetails = TechnicalDetailsSettings.TryReadOrDefault(WindowStateRoot());

        // The floor is small enough for a 1366 by 768 laptop with its taskbar showing, and for a
        // 1024 by 768 display: Windows will not honour a minimum larger than the screen, and a
        // window that cannot fit cannot be used. Above the floor the window opens as large as the
        // working area allows, up to its preferred size, stays resizable, and reflows.
        MinimumSize = new Size(MinimumWindowWidth, MinimumWindowHeight);
        Rectangle workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, PreferredWindowWidth, PreferredWindowHeight);
        Width = Math.Max(MinimumWindowWidth, Math.Min(PreferredWindowWidth, workingArea.Width));
        Height = Math.Max(MinimumWindowHeight, Math.Min(PreferredWindowHeight, workingArea.Height));
        StartPosition = FormStartPosition.CenterScreen;

        // A row shows a plain name and one line saying what the test proves, plus its state, not
        // the TestId alone. State is the second column and every column is sized to fit inside
        // leftPanel's own width, so the state is always on screen at the default window size,
        // never behind a horizontal scroll; the full text of a long name or line is always
        // available in full underneath, in _rowDetailLabel.
        _rowList = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false,
            MultiSelect = false,
        };
        _rowList.Columns.Add("#", 50);
        _rowList.Columns.Add("State", 150);
        _rowList.Columns.Add("Test", 280);
        _rowList.Columns.Add("What it proves", 520);
        _rowList.SelectedIndexChanged += (_, _) => { UpdateStartButton(); UpdateRowDetail(); };

        _rowDetailLabel = new Label { Dock = DockStyle.Bottom, Height = 110, AutoEllipsis = false, TextAlign = ContentAlignment.TopLeft };

        _speakerChoiceLabel = new Label
        {
            Dock = DockStyle.Bottom, Height = 20, TextAlign = ContentAlignment.MiddleLeft, ForeColor = SystemColors.GrayText,
            Text = Copy.SpeakerAddressChoicePrompt, Visible = false,
        };
        _speakerChoiceRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, FlowDirection = FlowDirection.LeftToRight, Visible = false };

        // The full-window-width List view's own row-list panel (RowListLayoutTests reads this
        // literal, by name and by its own Width, straight out of this source file).
        var leftPanel = new Panel { Dock = DockStyle.Fill, Width = 1040 };
        leftPanel.Controls.Add(_rowList);

        var listBottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 160 };
        listBottomPanel.Controls.Add(_rowDetailLabel);
        listBottomPanel.Controls.Add(_speakerChoiceRow);
        listBottomPanel.Controls.Add(_speakerChoiceLabel);

        _startButton = new Button { Text = Copy.StartThisTestButtonLabel, Width = 160, Height = 36, Margin = new Padding(4) };
        _startButton.Click += (_, _) => StartSelectedRow();

        _backButton = new Button { Text = Copy.ListBackButtonLabel, Width = 100, Height = 36, Margin = new Padding(4) };
        _backButton.Click += (_, _) => SwitchToView(MainView.Home);

        _notedStartWarningLabel = new Label
        {
            Dock = DockStyle.Bottom, Height = 48, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DarkRed, Visible = false,
        };
        _notedStartButton = new Button { Text = Copy.NotedStartButtonLabel, Dock = DockStyle.Bottom, Height = 28, Visible = false };
        _notedStartButton.Click += (_, _) => ProceedWithNotedStart();

        var listButtonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
        };
        listButtonRow.Controls.Add(_startButton);
        listButtonRow.Controls.Add(_backButton);

        _listPanel = new Panel { Dock = DockStyle.Fill, Visible = false };
        _listPanel.Controls.Add(leftPanel);
        _listPanel.Controls.Add(listBottomPanel);
        _listPanel.Controls.Add(listButtonRow);
        _listPanel.Controls.Add(_notedStartButton);
        _listPanel.Controls.Add(_notedStartWarningLabel);

        // The form only renders the current prompt, the transcript box and a Stop
        // button, standing throughout a run, not only for the rare unrecognised-prompt case
        // StepPanel's own built-in stop button covers. Small and pinned to the bottom edge of the
        // Running view (the layout's Step view), away from StepPanel's own,
        // much larger answer buttons above it, rather than docked above everything the way it used
        // to sit: "Stop this test" must never read as one of the answers to whatever question is
        // currently on screen.
        _stopButton = new Button { Text = "Stop the test", AutoSize = true, Height = 24, Enabled = false, Visible = false };
        _stopButton.Click += (_, _) => OnStopClicked();

        _watchdogTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _watchdogTimer.Tick += (_, _) => OnWatchdogTick();
        _watchdogTimer.Start();

        _caseBoxLabel = new Label
        {
            AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Text = Copy.PracticeDataLabel, Visible = sandbox is not null,
        };
        _caseBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Visible = sandbox is not null, Width = 120 };
        _caseBox.Items.AddRange(new object[] { "none", "one", "two" });
        _caseBox.SelectedIndex = 1;

        // AutoEllipsis used to cut a long path with "..." unconditionally (the layout's
        // own "What is wrong now": the exe path was always-visible clutter). It is shown only when
        // technical details is on (ChooseExePath, OnTechnicalDetailsToggled and the initial value
        // below all set its Visible the same way), and AutoSize with a wide MaximumSize lets it wrap
        // and grow rather than clip, however long the path is.
        _exePathLabel = new Label
        {
            AutoSize = true, MaximumSize = new Size(900, 0), TextAlign = ContentAlignment.MiddleLeft, ForeColor = SystemColors.GrayText,
            AutoEllipsis = false, Text = "Earshot.exe: " + _exePath, Visible = _showTechnicalDetails,
        };
        _chooseExeButton = new Button { Text = Copy.FindEarshotButtonLabel, AutoSize = true };
        _chooseExeButton.Click += (_, _) => ChooseExePath();

        _technicalDetailsCheckBox = new CheckBox
        {
            Text = Copy.ShowTechnicalDetailsLabel, AutoSize = true, Checked = _showTechnicalDetails,
        };
        _technicalDetailsCheckBox.CheckedChanged += (_, _) => OnTechnicalDetailsToggled();

        // 32 px only ever fit one short line: PowerCycleGate.RefusalMessage's own ~230-character
        // sentence wraps to more lines than that at this window's own (now smaller) width, and the
        // rest was clipped, invisible, never scrolled to. A plain Label already wraps by width; the
        // only thing missing was room to show what it wrapped to.
        _statusLabel = new Label { Dock = DockStyle.Bottom, Height = 64, TextAlign = ContentAlignment.MiddleLeft };
        _bannerLabel = new Label
        {
            AutoSize = true, MaximumSize = new Size(1040, 0), TextAlign = ContentAlignment.MiddleLeft, Visible = false,
            Font = new Font(Font, FontStyle.Bold), AutoEllipsis = false,
        };
        _bannerHowToStepsLabel = new Label
        {
            AutoSize = true, MaximumSize = new Size(700, 0), TextAlign = ContentAlignment.TopLeft, Visible = false,
            Text = string.Join(Environment.NewLine, Copy.AtRestHowToSteps.Select((step, index) => (index + 1) + ". " + step)),
        };
        _bannerHowToPictureBox = new PictureBox
        {
            Width = 160, Height = 160, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle, Visible = false,
            Image = HowToPictures.TryLoad("earshot-icon-taskbar"),
        };
        var bannerHowToRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        bannerHowToRow.Controls.Add(_bannerHowToStepsLabel);
        bannerHowToRow.Controls.Add(_bannerHowToPictureBox);

        var contentHost = new Panel { Dock = DockStyle.Fill };
        _stepPanel = new StepPanel { Visible = false };
        // The silence watchdog must stop treating a reply as though the prompt it answered
        // were still pending. Without this, _currentPromptSeq was set once by the first prompt and
        // never cleared again until the whole run ended, so OnWatchdogTick's own "no prompt
        // pending" gate was permanently false from the second prompt onwards: the watchdog could
        // never fire again for the rest of any run.
        _stepPanel.ReplySent += seq =>
        {
            if (_currentPromptSeq == seq)
            {
                _currentPromptSeq = null;
                _lastActivityUtc = DateTimeOffset.UtcNow;
            }
        };
        _resultPanel = new ResultPanel { Visible = false };
        _handOffBox = new TextBox
        {
            Multiline = true, ReadOnly = true, WordWrap = true, Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Vertical, Visible = false, BackColor = Color.White,
            Font = new Font(Font.FontFamily, 11f),
        };
        contentHost.Controls.Add(_handOffBox);
        contentHost.Controls.Add(_resultPanel);
        contentHost.Controls.Add(_stepPanel);

        _runAllCarryOnButton = new Button { Text = Copy.RunAllCarryOnButtonLabel, Visible = false, AutoSize = true, Margin = new Padding(4) };
        _runAllCarryOnButton.Click += (_, _) => OnRunAllCarryOnClicked();

        _runAllStopHereButton = new Button { Text = Copy.RunAllStopHereButtonLabel, Visible = false, AutoSize = true, Margin = new Padding(4) };
        _runAllStopHereButton.Click += (_, _) => OnRunAllStopHereClicked();

        // "Back to the start" (Home, not List: see the field's own remark): visible exactly when
        // the Result view is on screen and Run all's own two buttons above are not
        // (UpdateBackToStartVisibility, called at every place any of the three changes).
        _backToStartButton = new Button { Text = Copy.BackToStartButtonLabel, Visible = false, AutoSize = true, Margin = new Padding(4) };
        _backToStartButton.Click += (_, _) => SwitchToView(MainView.Home);

        _runAllStatusLabel = new Label
        {
            AutoSize = true, MaximumSize = new Size(1040, 0), TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DarkSlateBlue,
        };
        _runAllProgressLabel = new Label
        {
            AutoSize = true, MaximumSize = new Size(1040, 0), TextAlign = ContentAlignment.MiddleLeft, ForeColor = SystemColors.GrayText, Visible = false,
        };

        _runAllRestoreAdviceLabel = new Label
        {
            AutoSize = true, MaximumSize = new Size(1040, 0), TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DarkRed, Visible = false,
        };
        _runAllRestoreContinueButton = new Button
        {
            Text = Copy.RunAllRestoreContinueButtonLabel, Visible = false, AutoSize = true, Margin = new Padding(4),
        };
        _runAllRestoreContinueButton.Click += (_, _) => OnRunAllRestoreContinueClicked();

        _runAllExplanationLabel = new Label
        {
            AutoSize = true, MaximumSize = new Size(1040, 0), TextAlign = ContentAlignment.MiddleLeft, ForeColor = SystemColors.GrayText,
            Text = Copy.RunAllExplanation,
        };

        // The window's main control: one large primary button on Home, taller and bolder than
        // every other button here, for running every test back to back; the row list and its own
        // Start button stay secondary, reached through "Choose one test", for running one test.
        _runAllButton = new Button { Text = Copy.RunAllButtonLabel, Width = 320, Height = 56, Font = new Font(Font.FontFamily, 12f, FontStyle.Bold) };
        _runAllButton.Click += (_, _) => StartOrContinueRunAll();

        // Beside row 15 rather than inside the row list, since this is a utility check, not
        // one of the 16 numbered tests. Disabled outright in a sandbox window (never Visible at
        // all is not enough on its own; StartRehearsal itself refuses too, belt and braces): an
        // unattended or development sandbox must never run this, because it always raises a real
        // Windows administrator prompt, in any mode.
        _rehearsalRow = BuildRehearsalDisplayRow();
        _rehearsalTeaserLabel = new Label { AutoSize = true, MaximumSize = new Size(700, 0), TextAlign = ContentAlignment.MiddleLeft, Text = Copy.RehearsalTeaser };
        _rehearsalWarningLabel = new Label
        {
            AutoSize = true, MaximumSize = new Size(700, 0), TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DarkRed,
            Text = Copy.RehearsalWarning,
        };
        _rehearsalStatusLabel = new Label
        {
            AutoSize = true, MaximumSize = new Size(700, 0), TextAlign = ContentAlignment.MiddleLeft, ForeColor = SystemColors.GrayText,
            Text = sandbox is null ? string.Empty : Copy.RehearsalNeverInSandbox,
        };
        _rehearsalButton = new Button
        {
            Text = Copy.RehearsalRowName, AutoSize = true, Enabled = sandbox is null,
        };
        _rehearsalButton.Click += (_, _) => StartRehearsal();

        // The collapsed "More" section: technical details, the exe chooser (path shown only with
        // technical details on), the permission box check (a short teaser always shown, the fuller
        // warning only once this section is open) and, in a practice window only, the fake-input
        // case chooser.
        _morePanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Visible = false,
        };
        _morePanel.Controls.Add(_technicalDetailsCheckBox);
        _morePanel.Controls.Add(_chooseExeButton);
        _morePanel.Controls.Add(_exePathLabel);
        _morePanel.Controls.Add(_rehearsalButton);
        _morePanel.Controls.Add(_rehearsalTeaserLabel);
        _morePanel.Controls.Add(_rehearsalStatusLabel);
        _morePanel.Controls.Add(_rehearsalWarningLabel);
        if (sandbox is not null)
        {
            _morePanel.Controls.Add(_caseBoxLabel);
            _morePanel.Controls.Add(_caseBox);
        }

        _moreToggleButton = new Button { Text = Copy.MoreSectionShowLabel, AutoSize = true };
        _moreToggleButton.Click += (_, _) => ToggleMoreSection();

        _chooseOneTestLinkButton = new Button { Text = Copy.ChooseOneTestLinkLabel, AutoSize = true, FlatStyle = FlatStyle.Flat };
        _chooseOneTestLinkButton.FlatAppearance.BorderSize = 0;
        _chooseOneTestLinkButton.Click += (_, _) => SwitchToView(MainView.List);

        var homeTitleLabel = new Label { AutoSize = true, Font = new Font(Font.FontFamily, 16f, FontStyle.Bold), Text = Copy.HomeTitle };
        var homeIntroLabel1 = new Label { AutoSize = true, MaximumSize = new Size(900, 0), Text = Copy.HomeIntroWhatEarshotDoes };
        var homeIntroLabel2 = new Label { AutoSize = true, MaximumSize = new Size(900, 0), Text = Copy.HomeIntroWhatTheseTestsAreFor };
        var homeIntroLabel3 = new Label { AutoSize = true, MaximumSize = new Size(900, 0), Text = Copy.HomeIntroHowThisWindowHelps };
        _homeCountLineLabel = new Label { AutoSize = true, MaximumSize = new Size(900, 0), ForeColor = SystemColors.GrayText };
        _homePickupLineLabel = new Label { AutoSize = true, MaximumSize = new Size(900, 0), Font = new Font(Font, FontStyle.Bold), Visible = false };

        var homeFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(16),
        };
        homeFlow.Controls.Add(_bannerLabel);
        homeFlow.Controls.Add(bannerHowToRow);
        homeFlow.Controls.Add(homeTitleLabel);
        homeFlow.Controls.Add(homeIntroLabel1);
        homeFlow.Controls.Add(homeIntroLabel2);
        homeFlow.Controls.Add(homeIntroLabel3);
        homeFlow.Controls.Add(_runAllButton);
        homeFlow.Controls.Add(_homeCountLineLabel);
        homeFlow.Controls.Add(_runAllExplanationLabel);
        homeFlow.Controls.Add(_homePickupLineLabel);
        homeFlow.Controls.Add(_chooseOneTestLinkButton);
        homeFlow.Controls.Add(_moreToggleButton);
        homeFlow.Controls.Add(_morePanel);

        _homePanel = new Panel { Dock = DockStyle.Fill };
        _homePanel.Controls.Add(homeFlow);

        var runningTopFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        runningTopFlow.Controls.Add(_runAllProgressLabel);
        runningTopFlow.Controls.Add(_runAllStatusLabel);
        runningTopFlow.Controls.Add(_runAllRestoreAdviceLabel);
        runningTopFlow.Controls.Add(_runAllRestoreContinueButton);
        runningTopFlow.Controls.Add(_runAllCarryOnButton);
        runningTopFlow.Controls.Add(_runAllStopHereButton);
        runningTopFlow.Controls.Add(_backToStartButton);

        // "Stop this test", small, at the bottom edge of the whole Running view, away from
        // StepPanel's own answer buttons above it (the layout's Step view).
        var stopRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 6, 8, 6),
        };
        stopRow.Controls.Add(_stopButton);

        _runningPanel = new Panel { Dock = DockStyle.Fill, Visible = false };
        _runningPanel.Controls.Add(contentHost);
        _runningPanel.Controls.Add(runningTopFlow);
        _runningPanel.Controls.Add(stopRow);

        var viewHost = new Panel { Dock = DockStyle.Fill };
        viewHost.Controls.Add(_runningPanel);
        viewHost.Controls.Add(_listPanel);
        viewHost.Controls.Add(_homePanel);

        Controls.Add(viewHost);
        Controls.Add(_statusLabel);

        FormClosing += OnFormClosing;
        Activated += OnWindowActivated;

        PopulateRows();
        UpdateStartButton();
        UpdateRowDetail();
        SwitchToView(MainView.Home);
    }

    // Exactly one of the three view panels is ever Visible; nothing about what a control does
    // changes, only whether it is currently reachable by a real click (WinForms' own Visible
    // getter folds a hidden parent into every child control's own Visible read). Called by every
    // production path that starts a run (BeginRun) or opens the guided Run all flow
    // (StartOrContinueRunAll/ShowRunAllRestoreAdvice), by the Home/List navigation controls
    // themselves, and by the small Ensure/Show*ForTests seams a handful of existing tests need so a
    // click they already perform still reaches a control that now lives in a specific view.
    private void SwitchToView(MainView view)
    {
        _currentView = view;
        _homePanel.Visible = view == MainView.Home;
        _listPanel.Visible = view == MainView.List;
        _runningPanel.Visible = view == MainView.Running;
    }

    private void ToggleMoreSection()
    {
        _moreOpen = !_moreOpen;
        _morePanel.Visible = _moreOpen;
        _moreToggleButton.Text = _moreOpen ? Copy.MoreSectionHideLabel : Copy.MoreSectionShowLabel;
    }

    // Home's own "how many tests" line and, while a run is waiting after a shut down, which test
    // Carry on with the tests will pick up at. Both are read fresh from real data (RunAllOrder.Items,
    // the same list RunAllProgressLine's own denominator already uses, and run-all.json), never a
    // typed number or name, so neither can drift from what Run all actually does. Called from
    // PopulateRows, so it is refreshed at every open, after every half and on every window Activated,
    // the same as the banner and the rehearsal status.
    private void UpdateHomeRunAllInfo()
    {
        int totalTests = RunAllOrder.Items.Count;
        int fullShutDownCount = RunAllOrder.Items.Count(item =>
        {
            DisplayRow? row = _displayRows.FirstOrDefault(r => r.RunAllKey == item.Key);
            return row is not null && row.PowerCycleRequirement == PowerCycleRequirement.FullShutDown;
        });
        _homeCountLineLabel.Text = Copy.HomeRunAllCountLine(totalTests, fullShutDownCount);

        RunAllRecord? existing = RunAllFile.TryRead(WindowStateRoot());
        DisplayRow? pickupRow = existing is not null && existing.StoppedAtIndex >= 0 && existing.StoppedAtIndex < RunAllOrder.Items.Count
            ? _displayRows.FirstOrDefault(r => r.RunAllKey == RunAllOrder.Items[existing.StoppedAtIndex].Key)
            : null;
        _homePickupLineLabel.Text = pickupRow is not null ? Copy.HomeCarryOnPickupLine(pickupRow.Name) : string.Empty;
        _homePickupLineLabel.Visible = pickupRow is not null;
    }

    // StepPanel's own progress line (the layout's Step view): "which test, of
    // how many", read from RunAllOrder.Items's own fixed sequence, the same list Run all's own
    // progress label and HomeRunAllCountLine already read. Works the same way whether this run is
    // part of Run all or an ordinary single Start click: RunAllOrder.Items names every numbered
    // row's fixed position regardless of how this run started, so a single test 03 start shows
    // "Test 3 of 20" exactly as Run all would when it reaches row 03. Row 00 Restore and the
    // administrator prompt check (row "AR") are not in RunAllOrder.Items at all (see RunAllOrder's
    // own remark: 00 is the manual escape hatch, never part of the guided sequence), so neither has
    // a position to report; this returns null for those, and StepPanel hides the line rather than
    // showing a fabricated one.
    private static string? ComputeStepProgressText(DisplayRow row)
    {
        for (int i = 0; i < RunAllOrder.Items.Count; i++)
        {
            if (RunAllOrder.Items[i].Key == row.RunAllKey)
            {
                return Copy.StepProgressLine(i + 1, RunAllOrder.Items.Count, row.Name);
            }
        }

        return null;
    }

    // A console run of one of the two commands rows 00 and 07 point the owner at
    // (Copy.RestoreUninstallOfferNotAvailable, Copy.PlanBNotAvailable) happens entirely outside
    // this window; the owner returning to it (alt-tab, clicking back onto it) is the moment this
    // window has any chance to notice what that run left on disk.
    private void OnWindowActivated(object? sender, EventArgs e)
    {
        PopulateRows();
        UpdateStartButton();
        UpdateRowDetail();
    }

    // Test seam: real callers never replace this; it defaults to a real OpenFileDialog filtered
    // to Earshot.exe, returning the chosen path or null when the owner cancelled. Substituting it
    // in a test proves ChooseExePath's own validation and persistence without ever putting a real
    // Windows file picker on screen, which nothing here can safely dismiss on its own.
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<string?> ChooseExePathDialogForTests { get; set; } = () =>
    {
        using var dialog = new OpenFileDialog { Filter = "Earshot.exe|Earshot.exe", CheckFileExists = true, Title = "Choose Earshot.exe" };
        return dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : null;
    };

    // Never trusts the dialog's own choice just because Windows let the owner pick it
    // (ExePathChoice: a local drive-letter path, a file that exists, named exactly Earshot.exe).
    // A valid choice replaces _exePath immediately and is remembered (ExePathSettings) so the
    // next open starts from it rather than the fixed %ProgramFiles% fallback again.
    private void ChooseExePath()
    {
        string? chosen = ChooseExePathDialogForTests();
        if (chosen is null)
        {
            return;
        }

        if (!ExePathChoice.IsValid(chosen, out string? reason))
        {
            _statusLabel.Text = _showTechnicalDetails ? "That choice was not used: " + reason : Copy.PlainExeChoiceNotUsedStatus;
            return;
        }

        _exePath = chosen;
        _exePathLabel.Text = "Earshot.exe: " + _exePath;
        _exePathLabel.Visible = _showTechnicalDetails;
        ExePathSettings.Write(WindowStateRoot(), _exePath);
        _statusLabel.Text = _showTechnicalDetails ? "Earshot.exe is now: " + _exePath : Copy.PlainExeChosenStatus;
        PopulateRows();
        UpdateStartButton();
    }

    // Persisted immediately, then whichever panel is actually on screen is redrawn from its own
    // cached, real presentation (never re-derived), so flipping the checkbox mid-step or
    // mid-result takes effect at once rather than waiting for the next prompt or run.
    private void OnTechnicalDetailsToggled()
    {
        _showTechnicalDetails = _technicalDetailsCheckBox.Checked;
        TechnicalDetailsSettings.Write(WindowStateRoot(), _showTechnicalDetails);
        _exePathLabel.Visible = _showTechnicalDetails;

        if (_lastPresentedPrompt is not null && _activeRunner is not null)
        {
            _stepPanel.Show(_activeRunner, _lastPresentedPrompt, _lastPresentedPromptSeq, _showTechnicalDetails, _lastPresentedPromptProgressText);
        }

        if (_lastResultPresentation is not null)
        {
            _resultPanel.Show(_lastResultPresentation, _showTechnicalDetails, _lastResultRowStateKind, _lastResultQualifier);
        }

        // The row list's own State column reads plain or technical the same way: PopulateRows
        // rebuilds every row's text from the same _showTechnicalDetails this handler just set.
        PopulateRows();
        UpdateRowDetail();
    }

    private void UpdateRowDetail()
    {
        if (_rowList.SelectedIndices.Count == 0)
        {
            _rowDetailLabel.Text = string.Empty;
            UpdateSpeakerChoice(null);
            ClearNotedStartWarningIfRowChanged(null);
            return;
        }

        int index = _rowList.SelectedIndices[0];
        if (index < 0 || index >= _displayRows.Count)
        {
            _rowDetailLabel.Text = string.Empty;
            UpdateSpeakerChoice(null);
            ClearNotedStartWarningIfRowChanged(null);
            return;
        }

        DisplayRow row = _displayRows[index];
        ClearNotedStartWarningIfRowChanged(row);

        // Name and Proves are this window's own plain words, always shown, in full: never cut
        // with an ellipsis. Title and Settles are the script's own words, so they move behind the
        // technical-details toggle; UpdateStartButton and every state derivation are unaffected,
        // since this method only ever builds display text.
        string text = row.Name + Environment.NewLine + Environment.NewLine + row.Proves;
        if (_showTechnicalDetails)
        {
            text += Environment.NewLine + Environment.NewLine + "Technical details" + Environment.NewLine +
                row.Title + Environment.NewLine + "Settles: " + row.Settles;
        }

        if (row.WaitsOnWindowsUpdate)
        {
            text += Environment.NewLine + Copy.WaitsOnWindowsUpdatePlain;
        }

        // A path this window never reaches must never be silently missing. Restore's own
        // uninstall offer and 07's plan B are real branches these scripts declare
        // (-OfferUninstall, -AllowPlanB) that this window never passes as true anywhere, so this
        // row can never reach them; said here rather than left for the owner to notice on his own.
        if (row.Number == "00")
        {
            text += Environment.NewLine + Environment.NewLine + Copy.RestoreUninstallOfferNotAvailable;
        }
        else if (row.Number == "07")
        {
            text += Environment.NewLine + Environment.NewLine + Copy.PlanBNotAvailable;
        }

        if (Test10VariantSandboxNote.ShouldShow(_sandbox is not null, row.Row.Number, row.VariantNumber))
        {
            text += Environment.NewLine + Environment.NewLine + Copy.Test10VariantNotSandboxTestable;
        }

        if (row.Row.Number == "14")
        {
            IReadOnlyList<SpeakerCandidate> candidates = SpeakerCandidateFinder.Find(LiveTestRoot());
            if (candidates.Count == 0)
            {
                text += Environment.NewLine + Environment.NewLine + Copy.SpeakerAddressNoCandidates;
            }

            UpdateSpeakerChoice(candidates);

            // The chosen candidate is shown on the row itself, before Start is ever clicked: the
            // owner reads what will actually be forwarded as -SpeakerAddress from the same place
            // as every other fact about this row, not only from which button happens to look
            // pressed in the choice row above.
            SpeakerCandidate? chosen = candidates.FirstOrDefault(c => c.Address == _chosenSpeakerAddress);
            text += Environment.NewLine + Environment.NewLine +
                (chosen is not null ? "Second device chosen: " + chosen.DisplayText : "Second device chosen: none.");
        }
        else
        {
            UpdateSpeakerChoice(null);
        }

        _rowDetailLabel.Text = text;
    }

    // Rebuilds the speaker-address choice buttons for test 14 (candidates is null for every other
    // row, or when nothing is selected). A previously chosen address is kept only while it is
    // still among the candidates read this time; anything else (no row 14, no candidates, a stale
    // choice) resets it to null, which StartFreshRun then reads as "no second device".
    private void UpdateSpeakerChoice(IReadOnlyList<SpeakerCandidate>? candidates)
    {
        _speakerChoiceRow.Controls.Clear();

        if (candidates is null || candidates.Count == 0)
        {
            _speakerChoiceRow.Visible = false;
            _speakerChoiceLabel.Visible = false;
            _chosenSpeakerAddress = null;
            return;
        }

        if (_chosenSpeakerAddress is not null && !candidates.Any(c => c.Address == _chosenSpeakerAddress))
        {
            _chosenSpeakerAddress = null;
        }

        _speakerChoiceLabel.Visible = true;
        _speakerChoiceRow.Visible = true;

        foreach (SpeakerCandidate candidate in candidates)
        {
            string captured = candidate.Address;
            var button = new Button { Text = candidate.DisplayText, AutoSize = true, Margin = new Padding(4) };
            button.Click += (_, _) => { _chosenSpeakerAddress = captured; UpdateRowDetail(); };
            _speakerChoiceRow.Controls.Add(button);
        }

        var noneButton = new Button { Text = Copy.SpeakerAddressChoiceNone, AutoSize = true, Margin = new Padding(4) };
        noneButton.Click += (_, _) => { _chosenSpeakerAddress = null; UpdateRowDetail(); };
        _speakerChoiceRow.Controls.Add(noneButton);
    }

    // Closing while the banner is red asks first. Closing during a run asks first too, and
    // closing anyway is the same as a forced kill (the process is going away either way; the row
    // must read Unknown afterwards, not whatever it said before this run started).
    // Test seam: real callers never replace this; it defaults to the real modal box every
    // OnFormClosing decision used to call directly. Substituting it in a test proves what
    // OnFormClosing decided to do without ever putting a real dialog on screen, which nothing
    // here can safely dismiss on its own.
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<string, DialogResult> ConfirmDialogForTests { get; set; } =
        message => MessageBox.Show(message, "Earshot live tests", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

    // Test seam: null in every real run, where StartResumedSecondHalf always reads this
    // machine's own real event log through RunPowerCycleProbe. Set, it is read instead, so a
    // test can choose the verdict a resumed half sees (a restart where a full shut down was
    // required, for example) without depending on what this machine's own history happens to
    // say right now.
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<PowerCycleVerdict>? PowerCycleVerdictOverrideForTests { get; set; }

    // When Windows ends the session while a half is running, the form cancels the close once, so
    // Windows shows its own "this app is preventing shut down" screen with the window title
    // "Earshot live tests: a test is still running". If the owner forces it, the child dies with
    // the session and the next open shows the red banner. Windows sends WM_QUERYENDSESSION and
    // waits for FormClosing to answer synchronously; a modal MessageBox does not answer it, it
    // blocks the answer, so this path never shows one. The cancel happens
    // at most once per session-end attempt: a second attempt (the owner having forced it, or
    // Windows trying again) is let through, so the window can never make itself the one thing an
    // otherwise-successful shutdown cannot get past.
    private bool _windowsShutdownCancelledOnce;

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.WindowsShutDown)
        {
            if (_activeRunner is not null && !_windowsShutdownCancelledOnce)
            {
                _windowsShutdownCancelledOnce = true;
                Text = "Earshot live tests: a test is still running";
                e.Cancel = true;
            }

            return;
        }

        if (_activeRunner is not null)
        {
            DialogResult runChoice = ConfirmDialogForTests(Copy.CloseWhileRunningConfirmation);
            if (runChoice != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            KillActiveRun();
        }

        if (_banner.Level != BannerLevel.Red)
        {
            return;
        }

        DialogResult choice = ConfirmDialogForTests(Copy.CloseNotAtRestConfirmation);
        if (choice != DialogResult.Yes)
        {
            e.Cancel = true;
        }
    }

    private string LiveTestRoot() => _sandbox is not null
        ? Path.Combine(_sandbox.Folder, "local", "Earshot", "livetest")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Earshot", "livetest");

    // The window's own bookkeeping never lives at the root of livetest\: the harness's own
    // Copy-AppEvidence sweeps every *.json file it finds there and copies new ones into whichever
    // test folder is currently running, so a window-owned file at that root risked being swept
    // into a live test's own evidence by mistake. run-all.json lives in this folder instead,
    // beside but never inside the harness's own live test root.
    private string WindowStateRoot() => _sandbox is not null
        ? Path.Combine(_sandbox.Folder, "local", "Earshot", "livetest-gui")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Earshot", "livetest-gui");

    private void PopulateRows()
    {
        // Recomputed at every open and after every half.
        RefreshBanner();
        UpdateRehearsalStatus();
        UpdateRunAllButtonLabel();
        UpdateHomeRunAllInfo();

        int selected = _rowList.SelectedIndices.Count > 0 ? _rowList.SelectedIndices[0] : -1;
        _rowList.Items.Clear();
        foreach (DisplayRow row in _displayRows)
        {
            DerivedRowState state = ComputeState(row);
            string stateText = _showTechnicalDetails ? RowPresenter.Text(state) : RowPresenter.PlainText(state);
            var item = new ListViewItem(row.Number);

            // The cell shows a small symbol beside the state text, so colour is never the only
            // signal; the sub-item's own Tag keeps the plain state text alone (RowStateTextForTests
            // reads the Tag, so every test that pins an exact state string keeps working unchanged).
            ListViewItem.ListViewSubItem stateSubItem = item.SubItems.Add(RowPresenter.Symbol(state) + " " + stateText);
            stateSubItem.Tag = stateText;

            item.SubItems.Add(row.Name);
            item.SubItems.Add(row.Proves);
            item.ForeColor = state.IsGreen ? Color.DarkGreen : Color.Black;
            _rowList.Items.Add(item);
        }

        if (selected >= 0 && selected < _rowList.Items.Count)
        {
            _rowList.Items[selected].Selected = true;
        }
    }

    // After a shut down or an ordinary halt, run-all.json still records a stopped point to come
    // back to (RunAllFile), even though _runAllActive itself is only ever true while this window
    // is actually driving the sequence. The one primary button reads that record fresh at every
    // open, so reopening after the owner's own shut down shows "Carry on with the tests" rather
    // than the fresh-start label, with no second control to find.
    private void UpdateRunAllButtonLabel()
    {
        RunAllRecord? existing = RunAllFile.TryRead(WindowStateRoot());
        bool hasStoppedPoint = existing is not null && existing.StoppedAtIndex >= 0;
        _runAllButton.Text = hasStoppedPoint ? Copy.RunAllCarryOnLabel : Copy.RunAllButtonLabel;
    }

    // Recomputes the banner from disk right now and updates both the cached copy and the visible
    // label, then hands back that same fresh value. A start gate that instead read the cached
    // _banner field could still see whatever it was at the last PopulateRows: a console run of one
    // of the two commands Copy.RestoreUninstallOfferNotAvailable/PlanBNotAvailable point the owner
    // at (rows 00 and 07's own unavailable branches) can leave the machine not at rest while this
    // window stays open with its last banner still None, and Start started a child anyway. Every
    // start site (and BeginRun) calls this at the moment it actually checks RunGate.CanStart,
    // never the cached field, so a fresh disk read decides, not a stale in-memory one.
    private BannerState RefreshBanner()
    {
        _banner = Banner.Compute(LiveTestRoot(), _activeResultFolder);
        UpdateBannerLabel();
        return _banner;
    }

    // The at-rest banner sits at the top of Home, with its own
    // find-and-click-the-Earshot-icon how-to block and picture beside it; _bannerLabel is AutoSize
    // now, so only Visible needs setting here, never a fixed Height that could clip its own wrapped
    // text.
    private void UpdateBannerLabel()
    {
        if (_banner.Level == BannerLevel.None)
        {
            _bannerLabel.Visible = false;
            _bannerHowToStepsLabel.Visible = false;
            _bannerHowToPictureBox.Visible = false;
            return;
        }

        _bannerLabel.Text = _showTechnicalDetails ? _banner.Message ?? string.Empty : Copy.BannerPlainText(_banner.Level, _banner.RedCause);
        _bannerLabel.ForeColor = _banner.Level == BannerLevel.Red ? Color.White : Color.Black;
        _bannerLabel.BackColor = _banner.Level == BannerLevel.Red ? Color.Firebrick : Color.Goldenrod;
        _bannerLabel.Visible = true;
        _bannerHowToStepsLabel.Visible = true;
        _bannerHowToPictureBox.Visible = _bannerHowToPictureBox.Image is not null;
    }

    private DerivedRowState ComputeState(DisplayRow row)
    {
        if (IsElevationLocked(row))
        {
            return new DerivedRowState { Kind = RowStateKind.Locked, Reason = Copy.LockedDetail };
        }

        TestRowSpec spec = row.ToSpec();
        IReadOnlyList<RunEvidence> evidence = EvidenceStore.LoadEvidence(LiveTestRoot(), row.TestId, _activeResultFolder);
        bool sequenceTimeDisagreement = EvidenceStore.SequenceDisagreesWithStampOrder(evidence);

        // The earlier-build check (StateDeriver's own EarlierBuildQualifier) means to compare the
        // exe a real run actually used against the one this window has chosen. In a practice
        // window the fake-device driver never runs the chosen exe at all; it always records its
        // own sandboxed stand-in path instead (Run-GuiHalfAgainstFakes.ps1's -ExePath forwarding),
        // so that comparison can never honestly match, and every row that had genuinely just
        // passed read "Worked, with an older copy of Earshot" regardless of anything the reader
        // did. Passed null here, exactly as AllScriptsAndHalvesThroughWindowTests already does
        // when it checks the same derivation directly: StateDeriver.Derive already treats a null
        // chosenExePath as "the earlier-build check does not apply", the same rule a real window
        // with no exe chosen yet already relies on.
        string? chosenExePath = _sandbox is null ? _exePath : null;
        DateTimeOffset? exeWrite = chosenExePath is not null && File.Exists(chosenExePath)
            ? File.GetLastWriteTimeUtc(chosenExePath)
            : null;
        return StateDeriver.Derive(spec, evidence, chosenExePath, exeWrite, sequenceTimeDisagreement);
    }

    // A hand-built row for the administrator prompt check, never one of the 16 in
    // Data\tests.json (Number "AR" so it can never collide with a real one, and so it is never
    // picked up by RunAllOrder, which walks the manifest's own numbered rows). Its TestId matches
    // ElevationGate.RehearsalTestId exactly, so ComputeState-style evidence lookups and the lock
    // rule agree on where its result.json lives.
    private static DisplayRow BuildRehearsalDisplayRow()
    {
        var row = new ManifestRow
        {
            Number = "AR",
            Script = "gui/Test-ElevatedLaunch.ps1",
            TestId = ElevationGate.RehearsalTestId,
            Kind = "utility",
            Halves = 1,
            Name = Copy.RehearsalRowName,
            Title = "Administrator prompt check",
            Proves = "This window's one elevated launch site can raise the Windows permission box and read the answer.",
            Settles = "Whether this window's one elevated launch site can raise the Windows permission box and read the answer, before test 15, 00's uninstall variant or 07's plan B ever depend on it.",
        };
        return new DisplayRow { Row = row };
    }

    // Never in a sandbox window (an unattended or development sandbox must never raise a
    // real Windows administrator prompt), never while another run is active (RunGate's own rule),
    // and always through the production driver: nothing about this site's own execution may be
    // faked.
    private void StartRehearsal()
    {
        if (_sandbox is not null)
        {
            _rehearsalStatusLabel.Text = Copy.RehearsalNeverInSandbox;
            return;
        }

        // Rehearsal is never exempt from the banner lock: it is not row 00, so nothing here ever
        // passes rowIsExemptFromBannerLock true.
        if (!RunGate.CanStart(_activeRunner, RefreshBanner().RowsLockedExceptRestore, rowIsExemptFromBannerLock: false))
        {
            return;
        }

        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            _statusLabel.Text = _showTechnicalDetails ? "Windows PowerShell 5.1 is not installed at " + host + "." : Copy.PlainPowerShellMissingStatus;
            return;
        }

        string liveTestRoot = LiveTestRoot();
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        string runRoot = Path.Combine(liveTestRoot, stamp);
        Directory.CreateDirectory(runRoot);

        string driver = RehearsalLaunch.DriverPath(_repoRoot);
        string scriptPath = RehearsalLaunch.ScriptPath(_repoRoot);
        var runner = new ChildRunner(host, driver, scriptPath, _exePath, runRoot, resume: false, variant: 0, offerUninstall: false, allowPlanB: false);
        BeginRun(_rehearsalRow, runner, Path.Combine(runRoot, ElevationGate.RehearsalTestId), isResume: false);
    }

    // Refreshed at every open and after every half (PopulateRows), the same as the banner: NotRun
    // until anything has ever been recorded, then whatever ElevationGate itself would say about
    // it, in this window's own words.
    private void UpdateRehearsalStatus()
    {
        if (_sandbox is not null)
        {
            return;
        }

        ParsedResult? rehearsal = ElevationGate.FindNewestRehearsal(LiveTestRoot());
        DateTimeOffset harnessNewestWriteUtc = ElevationGate.HarnessNewestWriteUtc(_repoRoot);
        bool unlocked = ElevationGate.IsUnlocked(rehearsal, harnessNewestWriteUtc);

        _rehearsalStatusLabel.Text = rehearsal switch
        {
            null => "Not yet run. " + Copy.RehearsalUnlocksRows,
            { Overall: "pass" } when unlocked => "Passed. " + Copy.RehearsalUnlocksRows,
            { Overall: "pass" } => "Passed, but an older one: run it again after this build's own harness files changed. " + Copy.RehearsalUnlocksRows,
            { Overall: "fail" } => "Failed. " + Copy.RehearsalUnlocksRows,
            _ => "Inconclusive. " + Copy.RehearsalUnlocksRows,
        };
    }

    // The lock rule, wired to row 15: its whole test is the elevated uninstall/install cycle, so
    // it is unambiguous. 00's uninstall variant and 07's plan B are the other two paths the same
    // elevated launch site would reach; this row does not give either its own locked row, so
    // neither is gated here either. UpdateRowDetail's own note on rows 00 and 07 says plainly
    // what is not offered, why, and exactly what to run instead from a console.
    private bool IsElevationLocked(DisplayRow row)
    {
        if (row.Row.Number != "15")
        {
            return false;
        }

        ParsedResult? rehearsal = ElevationGate.FindNewestRehearsal(LiveTestRoot());
        DateTimeOffset harnessNewestWriteUtc = ElevationGate.HarnessNewestWriteUtc(_repoRoot);
        return !ElevationGate.IsUnlocked(rehearsal, harnessNewestWriteUtc);
    }

    // A row of Halves 2 (04, 05, 08, 09, 15, and each of 10's five variants) is pending when its
    // newest run folder holds resume.txt, its result.json is a first-half result, and it has not
    // been set aside by a deliberate fresh start. Because each variant carries its own TestId, this can never
    // find another variant's pending run: "resume lands on the same row and the same variant" by
    // construction, not by an extra check here.
    private PendingRun? FindPendingRun(DisplayRow row) =>
        row.Halves == 2 ? PendingRunFinder.Find(row.ToSpec(), LiveTestRoot(), _activeResultFolder) : null;

    private void UpdateStartButton()
    {
        int index = _rowList.SelectedIndices.Count > 0 ? _rowList.SelectedIndices[0] : -1;
        if (index < 0 || index >= _displayRows.Count)
        {
            _startButton.Enabled = false;
            _startButton.Text = Copy.StartThisTestButtonLabel;
            return;
        }

        DisplayRow row = _displayRows[index];
        // The red banner locks every row except 00 Restore.
        bool lockedByBanner = RefreshBanner().RowsLockedExceptRestore && row.Row.Number != "00";

        PendingRun? pending = FindPendingRun(row);
        _startButton.Text = pending is not null ? Copy.CarryOnSecondHalfButtonLabel : Copy.StartThisTestButtonLabel;
        _startButton.Enabled = _activeRunner is null && !lockedByBanner && !IsElevationLocked(row);
    }

    private void StartSelectedRow()
    {
        int index = _rowList.SelectedIndices.Count > 0 ? _rowList.SelectedIndices[0] : -1;
        if (index < 0 || index >= _displayRows.Count)
        {
            return;
        }

        DisplayRow row = _displayRows[index];

        // Exactly one child may exist, and the red banner locks every row except 00 Restore: one
        // combined check, not only via the Start button's own Enabled state, because Run all and
        // its carry-on button call this same start path programmatically, never through a click a
        // disabled button could have blocked.
        if (!RunGate.CanStart(_activeRunner, RefreshBanner().RowsLockedExceptRestore, row.Row.Number == "00"))
        {
            return;
        }

        if (IsElevationLocked(row))
        {
            _statusLabel.Text = Copy.LockedDetail;
            return;
        }

        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            _statusLabel.Text = _showTechnicalDetails ? "Windows PowerShell 5.1 is not installed at " + host + "." : Copy.PlainPowerShellMissingStatus;
            return;
        }

        PendingRun? pending = FindPendingRun(row);
        if (pending is not null)
        {
            StartResumedSecondHalf(host, row, pending);
        }
        else
        {
            StartFreshRun(host, row);
        }
    }

    private void StartFreshRun(string host, DisplayRow row)
    {
        string liveTestRoot = LiveTestRoot();
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        string runRoot = Path.Combine(liveTestRoot, stamp);
        Directory.CreateDirectory(runRoot);
        string scriptPath = Path.Combine(_repoRoot, "tools", "live-tests", row.Script);

        // Test 14 only: the second device's address chosen from SpeakerAddressChoicesForTests's
        // own buttons (never typed), forwarded as -SpeakerAddress. Both drivers declare that
        // parameter and forward it to the target script only when it is not empty, exactly the
        // way -Variant already works for every other row.
        var extra = new List<(string Name, string Value)>();
        if (row.Row.Number == "14" && !string.IsNullOrEmpty(_chosenSpeakerAddress))
        {
            extra.Add(("SpeakerAddress", _chosenSpeakerAddress));
        }

        ChildRunner runner;
        if (_sandbox is not null)
        {
            string driver = Path.Combine(_repoRoot, "tools", "live-tests", "gui", "selftest", "Run-GuiHalfAgainstFakes.ps1");
            extra.Add(("SandboxRoot", _sandbox.Folder));
            extra.Add(("TestId", row.TestId));
            extra.Add(("Case", (string)_caseBox.SelectedItem!));
            runner = new ChildRunner(
                host, driver, scriptPath, _exePath, runRoot, resume: false, variant: row.VariantNumber, offerUninstall: false, allowPlanB: false,
                environmentOverrides: _sandbox.ChildEnvironment, extraArguments: extra);
        }
        else
        {
            string driver = Path.Combine(_repoRoot, "tools", "live-tests", "gui", "Invoke-GuiHalf.ps1");
            runner = new ChildRunner(
                host, driver, scriptPath, _exePath, runRoot, resume: false, variant: row.VariantNumber, offerUninstall: false, allowPlanB: false,
                extraArguments: extra.Count > 0 ? extra : null);
        }

        BeginRun(row, runner, Path.Combine(runRoot, row.TestId), isResume: false);
    }

    // A resumed run always uses the pending run's -RunRoot; the window never makes
    // a new root for a second half. resume.txt is parsed by ResumeFile, never executed; only its
    // four validated values (script, exe, root, variant) are ever used to start anything.
    private void StartResumedSecondHalf(string host, DisplayRow row, PendingRun pending)
    {
        string liveTestRoot = LiveTestRoot();
        string resumeTxtPath = Path.Combine(pending.Folder, "resume.txt");

        // This row's own script only, never any of the other 15: resume.txt is read off disk, and
        // a line naming a different (even if otherwise genuine) shipped script must never be
        // accepted just because it is one of the sixteen. Passing the whole list here used to let
        // that through, and would start a real child against the wrong script.
        string[] expectedScript = { row.Script };

        if (!ResumeFile.TryParse(resumeTxtPath, _repoRoot, liveTestRoot, expectedScript, out ResumeInstruction? instruction, out string? reason))
        {
            _statusLabel.Text = _showTechnicalDetails
                ? "resume.txt could not be used, so nothing was started: " + reason
                : Copy.PlainCouldNotContinueStatus;
            return;
        }

        // "since" is the first-half snapshot's finishedUtc, else resume.txt's last
        // write time.
        DateTimeOffset since = FirstHalfSnapshotFinishedUtc(pending.Folder, row.TestId) ?? File.GetLastWriteTimeUtc(resumeTxtPath);
        PowerCycleVerdict verdict;
        PowerCycleUnknownReason? unknownReason;
        if (PowerCycleVerdictOverrideForTests is { } overrideVerdict)
        {
            verdict = overrideVerdict();
            unknownReason = PowerCycle.ReasonForUnknown("{}");
            PowerCycleEvidenceFile.Write(pending.Folder, "{}", verdict);
        }
        else
        {
            string probeScript = Path.Combine(_repoRoot, "tools", "live-tests", "gui", "Get-PowerCycleEvidence.ps1");
            string evidenceJson = ChildRunner.RunPowerCycleProbe(host, probeScript, since, TimeSpan.FromSeconds(30));
            verdict = PowerCycle.Decide(evidenceJson);
            unknownReason = PowerCycle.ReasonForUnknown(evidenceJson);
            PowerCycleEvidenceFile.Write(pending.Folder, evidenceJson, verdict);
        }

        PowerCycleGateResult gate = PowerCycleGate.Evaluate(row.PowerCycleRequirement, verdict);
        if (gate == PowerCycleGateResult.Refuse)
        {
            _statusLabel.Text = PowerCycleGate.RefusalMessage(row.PowerCycleRequirement, verdict);
            PopulateRows();
            UpdateStartButton();
            return;
        }

        if (gate == PowerCycleGateResult.StartNoted)
        {
            ShowNotedStartWarning(host, row, instruction!, PowerCycleGate.NotedWarning(row.PowerCycleRequirement, verdict, unknownReason));
            return;
        }

        StartResumedChildRunner(host, row, instruction!);
    }

    // StartNoted's own gate: the row may still be tried, but only past a deliberate click on its
    // own warning, never silently, so a start that only ever needed noting can never look the same
    // as one that needed nothing at all. Cleared, not acted on, the moment a different row is
    // selected (UpdateRowDetail), so a stale warning from an earlier row can never be carried into
    // a click meant for a different one.
    private (string Host, DisplayRow Row, ResumeInstruction Instruction, string Warning)? _pendingNotedStart;

    private void ShowNotedStartWarning(string host, DisplayRow row, ResumeInstruction instruction, string warning)
    {
        _pendingNotedStart = (host, row, instruction, warning);
        _notedStartWarningLabel.Text = warning;
        _notedStartWarningLabel.Visible = true;
        _notedStartButton.Visible = true;
        _statusLabel.Text = Copy.NotedStartNeedsDeliberateClick;
    }

    private void ProceedWithNotedStart()
    {
        if (_pendingNotedStart is not { } pending)
        {
            return;
        }

        // The same combined gate every start path uses: reachable in a real window by starting
        // another run (the rehearsal, or a fresh Start on a different row) while this warning sits
        // pending, which used to leave two children alive at once. A refusal here leaves the
        // warning showing, since nothing about it has been acted on.
        if (!RunGate.CanStart(_activeRunner, RefreshBanner().RowsLockedExceptRestore, pending.Row.Row.Number == "00"))
        {
            return;
        }

        _pendingNotedStart = null;
        _notedStartWarningLabel.Visible = false;
        _notedStartButton.Visible = false;
        StartResumedChildRunner(pending.Host, pending.Row, pending.Instruction);
    }

    // A stale warning from an earlier row must never be carried into a click meant for a
    // different one: navigating away from the row it belongs to withdraws it rather than leaving
    // it clickable against whatever is selected now.
    private void ClearNotedStartWarningIfRowChanged(DisplayRow? row)
    {
        if (_pendingNotedStart is { } pending && !ReferenceEquals(pending.Row, row))
        {
            _pendingNotedStart = null;
            _notedStartWarningLabel.Visible = false;
            _notedStartButton.Visible = false;
        }
    }

    private void StartResumedChildRunner(string host, DisplayRow row, ResumeInstruction instruction)
    {
        ChildRunner runner;
        if (_sandbox is not null)
        {
            string driver = Path.Combine(_repoRoot, "tools", "live-tests", "gui", "selftest", "Run-GuiHalfAgainstFakes.ps1");
            var extra = new List<(string Name, string Value)>
            {
                ("SandboxRoot", _sandbox.Folder),
                ("TestId", row.TestId),
                ("Case", (string)_caseBox.SelectedItem!),
            };
            runner = new ChildRunner(
                host, driver, instruction.ScriptPath, instruction.ExePath, instruction.RunRoot, resume: true,
                variant: instruction.Variant ?? 0, offerUninstall: false, allowPlanB: false,
                environmentOverrides: _sandbox.ChildEnvironment, extraArguments: extra);
        }
        else
        {
            string driver = Path.Combine(_repoRoot, "tools", "live-tests", "gui", "Invoke-GuiHalf.ps1");
            runner = new ChildRunner(
                host, driver, instruction.ScriptPath, instruction.ExePath, instruction.RunRoot, resume: true,
                variant: instruction.Variant ?? 0, offerUninstall: false, allowPlanB: false);
        }

        BeginRun(row, runner, Path.Combine(instruction.RunRoot, row.TestId), isResume: true);
    }

    private static DateTimeOffset? FirstHalfSnapshotFinishedUtc(string folder, string testId)
    {
        string path = Path.Combine(folder, FirstHalfSnapshot.ResultFileName);
        (ParsedResult? result, _) = EvidenceStore.TryReadResult(path, testId);
        return result?.FinishedUtc;
    }

    // The one place every start path ends up before a child is actually made active: StartFreshRun,
    // StartResumedChildRunner (itself reached from both the ordinary and the noted-start path) and
    // StartRehearsal all funnel through here. Checking the combined gate again at this single
    // choke point, not only at each caller's own entry, means a future start path that forgets its
    // own check still cannot start a second child or bypass the banner: it has nowhere else to go
    // to actually begin one.
    private void BeginRun(DisplayRow row, ChildRunner runner, string resultFolder, bool isResume)
    {
        if (!RunGate.CanStart(_activeRunner, RefreshBanner().RowsLockedExceptRestore, row.Row.Number == "00"))
        {
            runner.Dispose();
            return;
        }

        _activeRunner = runner;
        _activeResultFolder = resultFolder;
        _activeIsResume = isResume;
        _activeSpec = row.ToSpec();
        _activeDisplayRow = row;
        _transcript.Clear();
        _currentPromptSeq = null;
        _killDeadlineUtc = null;
        _silenceWarningShown = false;
        _windowsShutdownCancelledOnce = false;
        _lastActivityUtc = DateTimeOffset.UtcNow;
        _stopButton.Enabled = true;
        _stopButton.Visible = true;
        _runAllButton.Enabled = false;

        if (_runAllActive)
        {
            // The stamp folder is resultFolder's own parent (<runRoot>\<TestId>): the pointer
            // run-all.json keeps for this item, keyed the same as
            // RunAllOrder's own item (RunAllKey, e.g. "10v3").
            string? stamp = Path.GetFileName(Path.GetDirectoryName(resultFolder));
            if (stamp is not null)
            {
                _runAllPointers[row.RunAllKey] = stamp;
            }
        }

        // Routed by identity, on the UI thread, at the moment each is actually handled, not
        // when it was queued. A message from a runner that is no longer _activeRunner (it ended,
        // or was superseded) is dropped rather than handled against whatever runner is active now.
        runner.MessageReceived += message => SafeBeginInvoke(() =>
        {
            if (RunGate.ShouldProcessMessage(_activeRunner, runner))
            {
                HandleMessage(row, message);
            }
        });
        runner.TranscriptLine += line => SafeBeginInvoke(() =>
        {
            if (RunGate.ShouldProcessMessage(_activeRunner, runner))
            {
                _transcript.Add(line);
                _lastActivityUtc = DateTimeOffset.UtcNow;
            }
        });

        // A process that ends without ever sending an exit or a crash message (killed
        // externally, torn down by Windows, or anything else that never reached Invoke-GuiHalf.ps1's
        // own finally): ReadLoopEnded's own ordering guarantee means this only ever still finds
        // _activeRunner set to this same runner when neither of those arrived first, since either
        // one would already have cleared it through OnRunFinished by the time this is handled.
        runner.ReadLoopEnded += () => SafeBeginInvoke(() =>
        {
            if (RunGate.ShouldProcessMessage(_activeRunner, runner) && _activeRunner is not null)
            {
                MarkUnknownAndReset("The test process ended without ever reporting a result. " + Copy.RowUnknownUntilItRunsAgain);
            }
        });

        _resultPanel.Visible = false;
        _handOffBox.Visible = false;
        _stepPanel.Visible = true;
        UpdateBackToStartVisibility();
        SwitchToView(MainView.Running);
        _statusLabel.Text = _showTechnicalDetails
            ? "Running " + row.TestId + (isResume ? " (second half)..." : "...")
            : Copy.PlainRunningStatus(row.Name, isResume);
        UpdateStartButton();

        // _activeRunner is already set (above) by the time this runs, so a throw here must never
        // simply unwind out of BeginRun: that left _activeRunner pointing at a runner whose
        // process never started, which RunGate.CanStart then read as a run still in progress,
        // refusing every further start including row 00 Restore, the only way off a red banner.
        // Caught, not swallowed: the raw exception is recorded (Trace) and its own type and
        // message are put in front of the owner, then this run is torn down exactly like a kill
        // (MarkUnknownAndReset), which also clears _activeRunner and leaves the window usable.
        //
        // Everything that can throw runs BEFORE runner.Start(): a throw after the real child is
        // already alive left this window believing nothing was running (MarkUnknownAndReset
        // clears _activeRunner) while the process itself kept going, orphaned, and RunGate let a
        // fresh start (Restore included) begin beside it. Written here even though Start() has not
        // run yet, so a throw from any of these three still lands in the same catch and the same
        // teardown; gui-run-started.txt genuinely meaning "started" is unaffected, since
        // MarkUnknownAndReset removes it again the moment this catch runs.
        try
        {
            // gui- prefixed like this window's other markers and never touching what the scripts
            // write (MarkUnknownAndReset already pre-creates this same folder for gui-killed.txt
            // the same way, so resultFolder not existing yet is not new here). Removed again by
            // OnRunFinished or MarkUnknownAndReset, whichever this window sees the half end
            // through; the one case that leaves it behind is the window dying together with its
            // own child (a forced session end, a power cut), which is exactly what it exists to
            // catch.
            Directory.CreateDirectory(resultFolder);
            File.WriteAllText(
                Path.Combine(resultFolder, "gui-run-started.txt"),
                DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));

            // Assigned once per half. A resumed second half (isResume true) takes a fresh number
            // here even though it reuses the first half's run folder: the two halves are different
            // events, and the run folder's ordering must reflect whichever one actually happened
            // last, not be frozen at whatever was true when the first half started.
            RunSequence.EnsureMarker(LiveTestRoot(), resultFolder, reissueForNewHalf: isResume);

            runner.Start();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "ChildRunner.Start() threw before " + row.TestId + " could run (recorded, not swallowed): " + ex);
            MarkUnknownAndReset(
                (_showTechnicalDetails
                    ? "Could not start " + row.TestId + ": " + ex.GetType().Name + ": " + ex.Message
                    : Copy.PlainCouldNotStartStatus(row.Name)) +
                " " + Copy.RowUnknownUntilItRunsAgain);
            // Whether runner.Start() itself threw, or never even ran because something before it
            // did: Dispose now kills a live process rather than only releasing the managed handle,
            // so a real child that did make it as far as starting is never left running, orphaned,
            // just because a later step in this same try block failed.
            runner.Dispose();
        }
    }

    // Removes gui-run-started.txt, if present: called from every path this window itself sees a
    // half end (OnRunFinished, MarkUnknownAndReset), so the marker is left behind only when the
    // window never got the chance to call either at all.
    private static void RemoveRunStartedMarker(string? resultFolder)
    {
        if (resultFolder is null)
        {
            return;
        }

        string path = Path.Combine(resultFolder, "gui-run-started.txt");
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private void HandleMessage(DisplayRow row, ChildMessage message)
    {
        _lastActivityUtc = DateTimeOffset.UtcNow;
        switch (message.Kind)
        {
            case ChildMessageKind.Hello:
                break;
            case ChildMessageKind.Prompt:
                // A prompt on screen is never a hang. While one is pending the
                // silence watchdog has nothing to say, and Stop's own behaviour changes: it can
                // abort this exact seq rather than only wait-then-kill.
                //
                // Any kill deadline armed for an earlier abort is withdrawn here too: aborting one
                // prompt does not always stop the script, since the shim's own throw on that abort
                // unwinds into the script's own finally, where the at-rest closing check can still
                // ask a genuine new question (offering to block the nodes) from the very same,
                // still-alive process. A new prompt arriving is proof the process answered, not
                // proof it is hung, so a deadline armed for the prompt it replaces must never
                // outlive it.
                _currentPromptSeq = message.Seq;
                _silenceWarningShown = false;
                _killDeadlineUtc = null;
                PresentedPrompt presented = PromptPresenter.Present(message, row.Row.Number, _wording, _transcript);
                _lastPresentedPrompt = presented;
                _lastPresentedPromptSeq = message.Seq;
                _lastPresentedPromptProgressText = ComputeStepProgressText(row);
                _stepPanel.Show(_activeRunner!, presented, message.Seq, _showTechnicalDetails, _lastPresentedPromptProgressText);
                break;
            case ChildMessageKind.Exit:
                // Provisional: OnRunFinished (called next, on this same thread) overwrites this
                // with the plain-mode row's own verdict once result.json has actually been read,
                // the same words RowPresenter shows in the list (Copy.PlainFinishedStatus).
                _statusLabel.Text = _showTechnicalDetails
                    ? row.TestId + " finished (exit " + message.ExitCode + ")."
                    : Copy.PlainRunningStatus(row.Name, _activeIsResume);
                OnRunFinished(row);
                break;
            case ChildMessageKind.Crash:
                _statusLabel.Text = _showTechnicalDetails
                    ? row.TestId + " crashed: " + message.Text
                    : Copy.PlainRunCrashedStatus(row.Name);
                OnRunFinished(row);
                break;
            case ChildMessageKind.Unreadable:
                _statusLabel.Text = _showTechnicalDetails
                    ? "Unreadable message: " + message.Text
                    : Copy.PlainUnreadableMessageStatus;
                break;
        }
    }

    private void OnRunFinished(DisplayRow row)
    {
        _stepPanel.Visible = false;
        // The child already sent its own exit message, so result.json is expected to be on disk
        // regardless of what happens next; a missed exit within this grace window (a lingering
        // handle, a slow-to-tear-down thread) must never leave the process itself orphaned,
        // holding stdin open, once this method moves on and drops the only reference to it.
        if (_activeRunner is not null && !_activeRunner.WaitForExit(ExitGracePeriodForTests))
        {
            _activeRunner.Kill();
        }

        // This window saw the half end (an exit or crash message arrived): the run-started
        // marker BeginRun wrote has done its job.
        RemoveRunStartedMarker(_activeResultFolder);

        string resultPath = Path.Combine(_activeResultFolder!, "result.json");
        (ParsedResult? result, string? failure) = EvidenceStore.TryReadResult(resultPath, row.TestId);

        // A declined start (No at Show-Preconditions, or any other stop before the script
        // ever reaches the point of writing resume.txt) still produces a readable result.json, but
        // there is nothing pending to come back to. The pending detection elsewhere already
        // requires resume.txt; the hand-off screen ("Now shut this computer down... it will pick
        // up here") must ask for exactly the same thing, not show it whenever this happened to be
        // a two-half test's first half regardless of what the run actually reached.
        bool wasFirstHalfOfTwoHalfTest = row.Halves == 2 && !_activeIsResume &&
            File.Exists(Path.Combine(_activeResultFolder!, "resume.txt"));
        if (result is not null && wasFirstHalfOfTwoHalfTest)
        {
            ShowHandOff(row, result);
        }
        else if (result is not null)
        {
            // The one source of truth for pass/fail/inconclusive/unknown: computed once here, fed
            // to the Result view's own verdict line and (below) reused for the plain status line,
            // rather than re-derived a second time and risking the two ever disagreeing.
            DerivedRowState state = ComputeState(row);
            _lastResultPresentation = ResultPresenter.Present(result, _activeResultFolder!, row.Row.Number, _wording);
            _lastResultRowStateKind = state.Kind;
            _lastResultQualifier = state.Qualifier;
            _resultPanel.Show(_lastResultPresentation, _showTechnicalDetails, state.Kind, state.Qualifier);
            _resultPanel.Visible = true;

            // The status line's own "Finished: ..." (plain mode only; technical mode already read
            // "<TestId> finished (exit N)." the moment the exit message arrived, and stays that
            // way): the same words RowPresenter/Copy.PlainRowText would show for this row in the
            // list right now, so this can never claim a better verdict than the list itself does.
            if (!_showTechnicalDetails)
            {
                _statusLabel.Text = Copy.PlainFinishedStatus(row.Name, RowPresenter.PlainText(state));
            }
        }
        else
        {
            _statusLabel.Text = _showTechnicalDetails
                ? "No readable result.json: " + failure
                : Copy.PlainNoReadableResultStatus(row.Name);
        }

        _activeRunner = null;
        _activeIsResume = false;
        _activeSpec = null;
        _activeDisplayRow = null;
        _currentPromptSeq = null;
        _killDeadlineUtc = null;
        _stopButton.Enabled = false;
        _stopButton.Visible = false;
        _runAllButton.Enabled = true;
        PopulateRows();
        UpdateStartButton();
        // Ordinary single-test path: Run all is not active, so neither branch below runs, and this
        // is the one place that decides "Back to the start" for it. The run-all branches below each
        // recompute this again themselves once they know their own outcome, harmlessly redundant
        // here.
        UpdateBackToStartVisibility();

        // The halt is decided from result.json, never from the click. Whatever
        // just finished, Run all (if active) re-derives this same item from disk and decides
        // afresh whether to stop here or move itself on; it never trusts what this method above
        // just did with the panels.
        if (_runAllActive && _runAllRecoveringViaRestore)
        {
            _runAllRecoveringViaRestore = false;
            OnRunAllRestoreRecoveryFinished(result);
        }
        else if (_runAllActive)
        {
            AdvanceRunAll();
        }
    }

    // Restore ran as Run all's own red-banner recovery step (started only after the owner clicked
    // through the same advice AtRestNo gives). Only leftAtRest of yes means this computer is
    // actually safe to carry on with the rest of the sequence; anything else (no, unknown, a
    // declined start, a killed run) halts here exactly as an ordinary failed row would, rather than
    // silently retrying Restore or moving on over a machine that is still not known to be at rest.
    private void OnRunAllRestoreRecoveryFinished(ParsedResult? result)
    {
        if (result?.LeftAtRest == "yes")
        {
            _runAllStatusLabel.Text = Copy.RunAllRestoreSucceeded;
            _runAllIndex = Math.Max(_runAllIndex, 0);
            SaveRunAllProgress();
            AdvanceRunAll();
            return;
        }

        _runAllActive = false;
        RunAllFile.Delete(WindowStateRoot());
        _runAllCarryOnButton.Visible = false;
        _runAllStopHereButton.Visible = false;
        _runAllProgressLabel.Visible = false;
        _runAllStatusLabel.Text = Copy.RunAllRestoreDidNotReachAtRest;
        UpdateBackToStartVisibility();
    }

    // Stop's own two paths. A prompt pending: send the real abort down the wire and
    // let the script's own catch/finally run, same as a real owner clicking Stop inside StepPanel
    // for an unrecognised prompt; nothing pending: there is no seq anything is waiting to read, so
    // waiting for it to arrive on its own would never end, and the second confirmation is asked
    // straight away.
    private void OnStopClicked()
    {
        if (_activeRunner is null)
        {
            return;
        }

        if (_currentPromptSeq is int seq)
        {
            _activeRunner.Abort(seq);
            _statusLabel.Text = Copy.StopSentWaitingStatus;
            _killDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(60);
        }
        else
        {
            ConfirmAndKill();
        }
    }

    private void OnWatchdogTick()
    {
        if (_activeRunner is null)
        {
            return;
        }

        if (_killDeadlineUtc is DateTimeOffset deadline && DateTimeOffset.UtcNow >= deadline)
        {
            _killDeadlineUtc = null;
            ConfirmAndKill();
            return;
        }

        // A hang is no prompt pending and no stdout line for MaxSilenceSeconds. The window
        // then asks, and does nothing by itself. Shown once per silent stretch, on the status
        // line rather than a modal, so it never blocks the Stop button it is telling the owner
        // about.
        if (_currentPromptSeq is null && !_silenceWarningShown)
        {
            int maxSilenceSeconds = _activeDisplayRow?.MaxSilenceSeconds ?? 900;
            if ((DateTimeOffset.UtcNow - _lastActivityUtc).TotalSeconds >= maxSilenceSeconds)
            {
                _silenceWarningShown = true;
                _statusLabel.Text = Copy.SilentForMinutesStatus(Math.Max(1, maxSilenceSeconds / 60));
            }
        }
    }

    private void ConfirmAndKill()
    {
        if (_activeRunner is null)
        {
            return;
        }

        DialogResult choice = MessageBox.Show(
            Copy.StopConfirmationWarning,
            "Earshot live tests", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (choice != DialogResult.Yes)
        {
            return;
        }

        KillActiveRun();
    }

    // Kill the process tree; the row is Unknown and the red banner requires Restore before
    // anything else. gui-killed.txt is what makes the row read Unknown rather than falling back
    // to an older, now-untrustworthy pass (StateDeriver.Derive's own newest-run check). It also
    // makes PendingRunFinder stop offering "Carry on" over the stale first-half result a killed
    // second half leaves behind, and makes Banner.Compute distrust that same stale result rather
    // than reading whatever leftAtRest it happens to carry as though it settled anything; a run
    // folder with no result.json at all was already red on its own, but this one does have a
    // (stale, untrustworthy) result.json, which needed its own check in both places.
    private void KillActiveRun()
    {
        if (_activeRunner is null)
        {
            return;
        }

        ChildRunner runner = _activeRunner;
        runner.Kill();

        // Kill() only asks the OS to terminate the process tree; ReadLoop's ReadLine keeps
        // running on its own ThreadPool thread until the killed process's stdout pipe actually
        // closes, a short time later, not synchronously with Kill() returning. This method is
        // called from OnFormClosing (a real close while a half is running) as well as from tests
        // that dispose the form right after calling it: either way, once this method returns, the
        // form may be disposed at any moment. Draining the read loop here, before that can
        // happen, is what SafeBeginInvoke's own disposal guard is the last line of defence for,
        // not the primary fix.
        try
        {
            runner.WaitForReadLoopAsync().Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException ex)
        {
            RecordPostDisposalDeliveryFailure(ex);
        }

        MarkUnknownAndReset("Stopped by force. " + Copy.RowUnknownUntilItRunsAgain);
    }

    // A process that ends without ever sending "type": "exit" or "type": "crash" (killed
    // externally, crashed before it could report, PowerShell itself torn down) is read exactly
    // like a forced kill: nothing here decided that, nothing was learned about the step it was
    // on, and nothing here can call Kill() on a process that is already gone, so this shares
    // KillActiveRun's own marker and reset rather than trying to stop it again.
    private void MarkUnknownAndReset(string statusText)
    {
        if (_activeRunner is null)
        {
            return;
        }

        DisplayRow? row = _activeDisplayRow;

        if (_activeResultFolder is not null)
        {
            Directory.CreateDirectory(_activeResultFolder);
            File.WriteAllText(Path.Combine(_activeResultFolder, "gui-killed.txt"), DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));

            // gui-killed.txt alone already explains this row; the run-started marker has done its
            // job the moment this window sees the half end by any path, this one included.
            RemoveRunStartedMarker(_activeResultFolder);
        }

        _stepPanel.Visible = false;
        _resultPanel.Visible = false;
        _handOffBox.Visible = false;
        _statusLabel.Text = statusText;

        _activeRunner = null;
        _activeIsResume = false;
        _activeSpec = null;
        _activeDisplayRow = null;
        _currentPromptSeq = null;
        _killDeadlineUtc = null;
        _stopButton.Enabled = false;
        _stopButton.Visible = false;
        _runAllButton.Enabled = true;
        PopulateRows();
        UpdateStartButton();

        // A forced kill, or a process ending on its own with nothing reported, is never a
        // script-decided outcome: Run all treats either one exactly like an ordinary halt on
        // failure, never advanced past on its own. A kill during the red-banner Restore recovery
        // step is never treated as a green light to carry on into the ordinary sequence: nothing
        // there has been confirmed at rest, so it is read the same as Restore itself failing to.
        if (_runAllActive && _runAllRecoveringViaRestore)
        {
            _runAllRecoveringViaRestore = false;
            OnRunAllRestoreRecoveryFinished(null);
        }
        else if (_runAllActive && row is not null)
        {
            HaltRunAll(row, new DerivedRowState { Kind = RowStateKind.Unknown, Reason = "stopped by force" });
        }
    }

    // Run all's guided sequence. Walks RunAllOrder from _runAllIndex; test 10's five
    // variants are ordinary items here (each is its own DisplayRow with its own RunAllKey), never
    // skipped. A row this window cannot start at all (Locked: only row 15, before the
    // administrator prompt check has passed) is skipped over with a plain reason rather than
    // halting the whole sequence for it; the end-of-sequence summary lists it under "not run yet"
    // all the same, since ComputeState still reads it as Locked on every later pass over the list.
    // It starts at most one child per call, then returns and waits for OnRunFinished to call back
    // in; it never loops past a row that is not yet a clean pass on disk.
    private void AdvanceRunAll()
    {
        // Never start a second child. AdvanceRunAll's only job is to start the next item, so
        // if one is already active this call has nothing to do (it will be called again from
        // OnRunFinished once that one ends).
        if (!RunGate.CanStart(_activeRunner))
        {
            return;
        }

        while (_runAllIndex >= 0 && _runAllIndex < RunAllOrder.Items.Count)
        {
            RunAllItem item = RunAllOrder.Items[_runAllIndex];
            DisplayRow? row = _displayRows.FirstOrDefault(r => r.RunAllKey == item.Key);
            if (row is null)
            {
                _runAllIndex++;
                continue;
            }

            DerivedRowState state = ComputeState(row);

            if (state.Kind == RowStateKind.Locked)
            {
                _runAllIndex++;
                continue;
            }

            // The caller's own decision, not just RunAllHalt.ShouldHalt in isolation. A
            // declined start (StoppedBeforeAnyStep) used to be treated as "fresh enough" to
            // start again, bypassing ShouldHalt (which already says halt for it) and restarting
            // the same test forever.
            RunAllAdvanceDecision decision = RunAllAdvance.Decide(state);
            if (decision == RunAllAdvanceDecision.Halt)
            {
                HaltRunAll(row, state);
                return;
            }

            if (decision == RunAllAdvanceDecision.Advance)
            {
                _runAllIndex++;
                continue;
            }

            SelectRow(row);
            string host = PowerShell51.ExecutablePath();
            if (!File.Exists(host))
            {
                HaltRunAll(row, state);
                return;
            }

            // Written before the child even starts, not only on a halt: closing the window mid
            // item must still leave run-all.json pointing at this same index, so reopening and
            // clicking "Carry on with the tests" again resumes here rather than from the start.
            SaveRunAllProgress();

            // The combined gate again, right before the item this loop actually chose is started:
            // a kill (or anything else) that turns the banner red between one call to AdvanceRunAll
            // and the next must stop Run all here rather than start the next item over a PC that is
            // not at rest. RunAllAdvance.Decide already read the evidence before the kill happened,
            // so it alone cannot see this.
            if (!RunGate.CanStart(_activeRunner, RefreshBanner().RowsLockedExceptRestore, row.Row.Number == "00"))
            {
                HaltRunAll(row, new DerivedRowState
                {
                    Kind = RowStateKind.Unknown,
                    Reason = "the at-rest banner locks every row except Restore until it clears",
                });
                return;
            }

            _runAllProgressLabel.Text = Copy.RunAllProgressLine(_runAllIndex + 1, RunAllOrder.Items.Count, row.Name);
            _runAllProgressLabel.Visible = true;

            PendingRun? pending = FindPendingRun(row);
            if (pending is not null)
            {
                StartResumedSecondHalf(host, row, pending);
            }
            else
            {
                StartFreshRun(host, row);
            }

            return;
        }

        _runAllActive = false;
        RunAllFile.Delete(WindowStateRoot());
        _runAllCarryOnButton.Visible = false;
        _runAllStopHereButton.Visible = false;
        _runAllProgressLabel.Visible = false;
        _runAllStatusLabel.Text = Copy.RunAllFinished + " " + BuildRunAllEndSummarySentence();
        UpdateBackToStartVisibility();
    }

    // Fresh from disk, every time: how many of Run all's own items ended up in each bucket right
    // now, whatever path each one took to get there (run for real, skipped as Locked, or never
    // reached at all), followed by which rows were never run and why.
    private string BuildRunAllEndSummarySentence()
    {
        int worked = 0, didNotWork = 0, couldNotTell = 0, notRunYet = 0;
        var notRunEntries = new List<string>();
        foreach (RunAllItem item in RunAllOrder.Items)
        {
            DisplayRow? row = _displayRows.FirstOrDefault(r => r.RunAllKey == item.Key);
            if (row is null)
            {
                continue;
            }

            DerivedRowState state = ComputeState(row);
            switch (RunAllSummary.Classify(state))
            {
                case RunAllSummaryBucket.Worked: worked++; break;
                case RunAllSummaryBucket.DidNotWork: didNotWork++; break;
                case RunAllSummaryBucket.CouldNotTell: couldNotTell++; break;
                default:
                    notRunYet++;
                    notRunEntries.Add(row.Number + " (" + row.Name + "): " + NotRunReasonText(state));
                    break;
            }
        }

        string sentence = Copy.RunAllSummarySentence(worked, didNotWork, couldNotTell, notRunYet);
        return notRunEntries.Count == 0 ? sentence : sentence + " " + string.Join(" ", notRunEntries);
    }

    private static string NotRunReasonText(DerivedRowState state) => state.Kind switch
    {
        RowStateKind.Locked => Copy.LockedDetail,
        RowStateKind.StoppedBeforeAnyStep => "It was stopped before any step ran.",
        RowStateKind.WaitingForShutDown => "It is waiting for the shut down.",
        RowStateKind.WaitingForRestart => "It is waiting for the restart.",
        _ => "It has not been run yet.",
    };

    private void HaltRunAll(DisplayRow row, DerivedRowState state)
    {
        SaveRunAllProgress();
        SelectRow(row);
        _runAllProgressLabel.Visible = false;

        bool atPowerCycleBoundary = state.Kind is RowStateKind.WaitingForShutDown or RowStateKind.WaitingForRestart;
        _runAllStatusLabel.Text = state.Kind == RowStateKind.Locked
            ? Copy.RunAllLockedItemSkipped + " " + Copy.LockedDetail
            : atPowerCycleBoundary
                ? Copy.RunAllStoppedForPowerCycle(row.Number)
                : Copy.RunAllStoppedForFailure(row.Number);

        // At the power-cycle boundary the row's own "Carry on with the second half" button
        // is the deliberate click that resumes Run all too, through
        // OnRunFinished -> AdvanceRunAll above; an ordinary failure needs its own
        // acknowledgement first, with the choice to give up on it too: until one of the two
        // buttons is pressed, Run all stays halted.
        _runAllCarryOnButton.Visible = !atPowerCycleBoundary;
        _runAllStopHereButton.Visible = !atPowerCycleBoundary;
        UpdateBackToStartVisibility();
    }

    private void OnRunAllCarryOnClicked()
    {
        // A stray or double click while a half is somehow already active must never start a
        // second one.
        if (!RunGate.CanStart(_activeRunner))
        {
            return;
        }

        _runAllCarryOnButton.Visible = false;
        _runAllStopHereButton.Visible = false;
        _runAllIndex++;
        SaveRunAllProgress();
        AdvanceRunAll();
    }

    // Gives up on the rest of the sequence at a halt, rather than the only other choice being to
    // carry on past whatever it stopped on: run-all.json is cleared, so a later "Run all the
    // tests" starts fresh rather than resuming at the row this abandoned.
    private void OnRunAllStopHereClicked()
    {
        _runAllActive = false;
        _runAllCarryOnButton.Visible = false;
        _runAllStopHereButton.Visible = false;
        _runAllProgressLabel.Visible = false;
        RunAllFile.Delete(WindowStateRoot());
        _runAllStatusLabel.Text = Copy.RunAllFinished + " " + BuildRunAllEndSummarySentence();
        UpdateRunAllButtonLabel();
        UpdateBackToStartVisibility();
    }

    // "Back to the start" is visible exactly when the Result view is on screen and Run all's own
    // two buttons are not: an ordinary single-test result (Run all not active), or Run all's own
    // end-of-sequence state (both natural completion and Stop here, where Run all is no longer
    // active but the last test's own result stays on screen). Never computed once and cached: this
    // is called again at every place any of the three inputs changes, so it can never drift from
    // what is actually on screen right now.
    private void UpdateBackToStartVisibility()
    {
        _backToStartButton.Visible = _resultPanel.Visible && !_runAllCarryOnButton.Visible && !_runAllStopHereButton.Visible;
    }

    private void SaveRunAllProgress()
    {
        RunAllFile.Write(WindowStateRoot(), new RunAllRecord
        {
            Order = RunAllOrder.Items.Select(RunAllFile.Key).ToArray(),
            StoppedAtIndex = _runAllIndex,
            Pointers = new Dictionary<string, string>(_runAllPointers, StringComparer.Ordinal),
        });
    }

    // The one entry point for the window's main control. The single-runner rule always applies;
    // the banner lock never simply refuses here the way every other start route's does, because
    // Run all's whole job under a red or unknown banner is to get this computer back to a known
    // state on its own, through row 00 Restore, the one start the banner lock already exempts.
    private void StartOrContinueRunAll()
    {
        if (!RunGate.CanStart(_activeRunner))
        {
            return;
        }

        if (RefreshBanner().RowsLockedExceptRestore)
        {
            ShowRunAllRestoreAdvice();
            return;
        }

        RunAllRecord? existing = RunAllFile.TryRead(WindowStateRoot());
        _runAllPointers.Clear();
        if (existing is not null)
        {
            foreach ((string key, string stamp) in existing.Pointers)
            {
                _runAllPointers[key] = stamp;
            }
        }

        _runAllActive = true;
        _runAllIndex = existing is not null && existing.StoppedAtIndex >= 0 ? existing.StoppedAtIndex : 0;
        _runAllCarryOnButton.Visible = false;
        _runAllStopHereButton.Visible = false;
        SwitchToView(MainView.Running);
        AdvanceRunAll();
    }

    // The deliberate step shown before Restore ever starts under a red or unknown banner: the same
    // cause-picked plain advice the at-rest banner itself gives (Copy.AtRestNoText), shown as a
    // step to act on first, since it is usually all that is needed; Restore itself is what runs
    // once the owner says they have done it, never silently skipped past.
    private void ShowRunAllRestoreAdvice()
    {
        _runAllRestoreAdviceLabel.Text = Copy.RunAllRestoreAdviceHeading + Environment.NewLine + Environment.NewLine + Copy.AtRestNoText(RefreshBanner().RedCause);
        _runAllRestoreAdviceLabel.Visible = true;
        _runAllRestoreContinueButton.Visible = true;
        _runAllStatusLabel.Text = string.Empty;
        SwitchToView(MainView.Running);
    }

    private void OnRunAllRestoreContinueClicked()
    {
        if (!RunGate.CanStart(_activeRunner))
        {
            return;
        }

        _runAllRestoreAdviceLabel.Visible = false;
        _runAllRestoreContinueButton.Visible = false;
        StartRunAllViaRestoreRecovery();
    }

    // Row 00 Restore, started as Run all's own red-banner recovery step: the one row the banner
    // lock already exempts, through the same single start gate every other route uses. Never adds
    // Restore to _runAllPointers (RunAllOrder's own sequence has no entry for "00" at all, by
    // design: Restore is the manual escape hatch, never one of the guided items).
    private void StartRunAllViaRestoreRecovery()
    {
        DisplayRow? restoreRow = _displayRows.FirstOrDefault(r => r.Row.Number == "00");
        if (restoreRow is null)
        {
            return;
        }

        if (!RunGate.CanStart(_activeRunner, RefreshBanner().RowsLockedExceptRestore, rowIsExemptFromBannerLock: true))
        {
            return;
        }

        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            _runAllStatusLabel.Text = _showTechnicalDetails ? "Windows PowerShell 5.1 is not installed at " + host + "." : Copy.PlainPowerShellMissingStatus;
            return;
        }

        _runAllActive = true;
        _runAllRecoveringViaRestore = true;
        _runAllIndex = -1;
        _runAllCarryOnButton.Visible = false;
        _runAllStopHereButton.Visible = false;
        _runAllStatusLabel.Text = Copy.RunAllRestoreRunning;
        SelectRow(restoreRow);
        StartFreshRun(host, restoreRow);
    }

    private void SelectRow(DisplayRow row)
    {
        for (int i = 0; i < _displayRows.Count; i++)
        {
            if (_displayRows[i].Number == row.Number)
            {
                _rowList.Items[i].Selected = true;
                _rowList.Items[i].EnsureVisible();
                return;
            }
        }
    }

    // The hand-off to the second half proceeds in order: read result.json; show leftAtRest; parse
    // resume.txt; copy result.json and summary.txt to their gui-first-half.* names; then, and only
    // then, the hand-off screen. The snapshot is taken here, additively, before anything else
    // touches this folder again.
    private void ShowHandOff(DisplayRow row, ParsedResult firstHalfResult)
    {
        FirstHalfSnapshot.Take(_activeResultFolder!);

        // The recorded reason lives in the leftAtRest finding's own Detail, not in a
        // separate member (LiveTest.psm1's Complete-LiveTestRun writes it there). Passing null
        // unconditionally meant "no-on-purpose" always showed "No reason was recorded." even when
        // the script had recorded one.
        string? leftAtRestReason = firstHalfResult.Findings.FirstOrDefault(f => f.Name == "leftAtRest")?.Detail;
        var lines = new List<string>
        {
            Copy.LeftAtRestText(firstHalfResult.LeftAtRest, leftAtRestReason),
            string.Empty,
        };

        if (Copy.FirstHalfGenuinelyFailed(firstHalfResult.Overall))
        {
            lines.Add(Copy.FirstHalfFailedHandOff);
        }
        else
        {
            lines.Add(Copy.HandOffText(row.PowerCycleRequirement));

            FindingRecord? fastStartup = firstHalfResult.Findings.FirstOrDefault(f => f.Name == "fastStartupAtPowerDown");
            if (fastStartup?.Value is not null)
            {
                lines.Add(string.Empty);
                lines.Add(Copy.FastStartupSentence(fastStartup.Value));
            }
        }

        _resultPanel.Visible = false;
        _handOffBox.Text = string.Join(Environment.NewLine, lines);
        _handOffBox.Visible = true;
        UpdateBackToStartVisibility();
        SwitchToView(MainView.Running);
        _statusLabel.Text = _showTechnicalDetails
            ? row.TestId + ": first half complete. Waiting for the power cycle."
            : Copy.PlainWaitingForPowerCycleStatus(row.PowerCycleRequirement, row.Name);
    }

    // The one call site every ChildRunner.MessageReceived and TranscriptLine closure must go
    // through, never a raw BeginInvoke. ReadLoop runs on its own ThreadPool thread and can still
    // be delivering a message the instant this form's handle is destroyed (Dispose, or a test
    // harness's own teardown right after it): a plain IsHandleCreated read is not enough, because
    // it can pass and then go stale before BeginInvoke actually runs, and a check on one thread
    // followed by a call on another cannot be made atomic against a Dispose happening in between.
    // The try/catch below is what is actually safe under that race. Anything it catches is
    // recorded, twice, never swallowed: PostDisposalDeliveryFailuresForTests for a test to read
    // (locked, since a background ReadLoop thread and this UI thread's own KillActiveRun can both
    // write to it), and a trace line for whatever is listening to this process's own diagnostic
    // output (Trace.Listeners holds at least the test host's own capture under a test run,
    // verified directly rather than assumed), because an uncaught exception here is unhandled on
    // a background thread by construction and takes the whole process down with it.
    private void SafeBeginInvoke(Action action)
    {
        try
        {
            BeginInvoke(action);
        }
        catch (ObjectDisposedException ex)
        {
            RecordPostDisposalDeliveryFailure(ex);
        }
        catch (InvalidOperationException ex)
        {
            RecordPostDisposalDeliveryFailure(ex);
        }
    }

    private void RecordPostDisposalDeliveryFailure(Exception ex)
    {
        lock (_postDisposalDeliveryFailuresGate)
        {
            _postDisposalDeliveryFailures.Add(ex);
        }

        System.Diagnostics.Trace.TraceWarning("A message delivery from ChildRunner arrived after this window's handle was gone: " + ex);
    }

    // Test seams only (Earshot.Tests, via InternalsVisibleTo): a fix whose only proof is an
    // extracted predicate in isolation proves nothing about the caller that used to bypass it.
    // These let a test drive this form's real click handlers headlessly (constructed, handle
    // forced, never Shown) and observe what a real click actually does, the same way this form's
    // own private methods are wired to controls.
    internal ChildRunner? ActiveRunnerForTests => _activeRunner;

    internal void SafeBeginInvokeForTests(Action action) => SafeBeginInvoke(action);

    internal IReadOnlyList<Exception> PostDisposalDeliveryFailuresForTests
    {
        get
        {
            lock (_postDisposalDeliveryFailuresGate)
            {
                return _postDisposalDeliveryFailures.ToArray();
            }
        }
    }

    // M9 test seams: drives the real silence watchdog tick and lets a test move "the last
    // activity was seen" into the past without a real wait, so both directions (fires on real
    // silence; never fires while a prompt is pending, however long) are provable in milliseconds.
    internal void ForceWatchdogTickForTests() => OnWatchdogTick();

    internal void SetLastActivityUtcForTests(DateTimeOffset utc) => _lastActivityUtc = utc;

    internal int? CurrentPromptSeqForTests => _currentPromptSeq;

    internal bool HasKillDeadlineForTests => _killDeadlineUtc is not null;

    internal void ClickCurrentPromptButtonForTests() => _stepPanel.ClickFirstButtonForTests();

    internal void ClickPromptButtonForTests(int index) => _stepPanel.ClickButtonForTests(index);

    internal string StepPanelAcknowledgementTextForTests => _stepPanel.AcknowledgementTextForTests;

    internal FormClosingEventArgs RaiseFormClosingForTests(CloseReason reason)
    {
        var args = new FormClosingEventArgs(reason, cancel: false);
        OnFormClosing(this, args);
        return args;
    }

    // Test seam: the real Activated handler, so a test can prove the banner and rows are
    // refreshed from disk without the headless form ever needing real Win32 focus, which
    // ForceControlCreationForTests's own real handle does not, by itself, guarantee raises.
    internal void RaiseActivatedForTests() => OnWindowActivated(this, EventArgs.Empty);

    internal bool BannerVisibleForTests => _bannerLabel.Visible;

    internal string BannerTextForTests => _bannerLabel.Text;

    // View-switching test seams (see the class remark and SwitchToView above). A later commit's
    // Step/Result tests build on these the same way.
    internal MainView CurrentViewForTests => _currentView;

    internal void ShowHomeViewForTests() => SwitchToView(MainView.Home);

    internal void ShowListViewForTests() => SwitchToView(MainView.List);

    internal Panel HomePanelForTests => _homePanel;

    internal Panel ListPanelForTests => _listPanel;

    internal void ClickChooseOneTestLinkForTests()
    {
        SwitchToView(MainView.Home);
        _chooseOneTestLinkButton.PerformClick();
    }

    internal void ClickBackButtonForTests()
    {
        SwitchToView(MainView.List);
        _backButton.PerformClick();
    }

    internal bool MoreSectionVisibleForTests => _morePanel.Visible;

    internal void ClickMoreToggleForTests()
    {
        SwitchToView(MainView.Home);
        _moreToggleButton.PerformClick();
    }

    // Ensures Home is showing and the collapsed More section is open, so a click on a control that
    // now lives inside it (the exe chooser, the rehearsal button) reaches a genuinely Visible
    // control, the same as a real owner opening More first would. Never changes what the click
    // itself proves: only whether it can reach the control at all (Button.PerformClick is a no-op
    // on an invisible control).
    private void EnsureHomeMoreExpandedForTests()
    {
        SwitchToView(MainView.Home);
        if (!_moreOpen)
        {
            _moreToggleButton.PerformClick();
        }
    }

    internal string HomeCountLineTextForTests => _homeCountLineLabel.Text;

    internal string HomePickupLineTextForTests => _homePickupLineLabel.Text;

    internal bool HomePickupLineVisibleForTests => _homePickupLineLabel.Visible;

    // Test seams for the rehearsal control. PerformClick (the same pattern every other *ForTests
    // click uses) does nothing on a disabled control (Button.CanSelect is false while Enabled is
    // false), so a click alone can never prove StartRehearsal's own runtime sandbox guard when the
    // button is disabled at construction, the way it always is in a sandbox window: a test wanting
    // to prove that guard fires even if something else left the button clickable has to force
    // Enabled true first (ForceRehearsalButtonEnabledForTests), then assert it, then click for real.
    internal bool RehearsalButtonEnabledForTests => _rehearsalButton.Enabled;

    internal void ForceRehearsalButtonEnabledForTests()
    {
        EnsureHomeMoreExpandedForTests();
        _rehearsalButton.Enabled = true;
    }

    internal string RowDetailTextForTests => _rowDetailLabel.Text;

    // Test seam: CheckBox exposes no PerformClick (that is a Button-only member); flipping Checked
    // is exactly what a real click does first, and raises the same CheckedChanged event that drives
    // the real OnTechnicalDetailsToggled handler, so this proves that handler rather than setting
    // _showTechnicalDetails directly.
    internal void ClickTechnicalDetailsCheckBoxForTests() => _technicalDetailsCheckBox.Checked = !_technicalDetailsCheckBox.Checked;

    internal bool ShowTechnicalDetailsForTests => _showTechnicalDetails;

    internal StepPanel StepPanelForTests => _stepPanel;

    internal ResultPanel ResultPanelForTests => _resultPanel;

    // Test 14's speaker-address choice: one entry per visible button (candidates, then "No second
    // device", in that order), the address PerformClick chose (null once cleared or before any
    // click), and the click itself, the same PerformClick pattern every other *ForTests click uses.
    internal IReadOnlyList<string> SpeakerAddressChoicesForTests =>
        _speakerChoiceRow.Controls.OfType<Button>().Select(button => button.Text).ToArray();

    internal string? ChosenSpeakerAddressForTests => _chosenSpeakerAddress;

    internal void ClickSpeakerAddressChoiceForTests(int index) =>
        (_speakerChoiceRow.Controls.Count > index ? _speakerChoiceRow.Controls[index] as Button : null)?.PerformClick();

    internal string RehearsalStatusTextForTests => _rehearsalStatusLabel.Text;

    internal string RehearsalWarningTextForTests => _rehearsalWarningLabel.Text;

    internal void ClickRehearsalButtonForTests()
    {
        EnsureHomeMoreExpandedForTests();
        _rehearsalButton.PerformClick();
    }

    internal bool HandOffVisibleForTests => _handOffBox.Visible;

    internal string HandOffTextForTests => _handOffBox.Text;

    internal bool ResultPanelVisibleForTests => _resultPanel.Visible;

    // Test seam: ShowHandOff needs only a DisplayRow and a ParsedResult, both constructible
    // without a real child, so the real production method (not a copy of its logic) is driven
    // directly for the case a live run cannot conveniently reach on its own (a specific
    // leftAtRest reason).
    internal void ShowHandOffForTests(DisplayRow row, ParsedResult firstHalfResult)
    {
        _activeResultFolder ??= Path.Combine(Path.GetTempPath(), "earshot-handoff-test-seam-" + Guid.NewGuid().ToString("N"), row.TestId);
        ShowHandOff(row, firstHalfResult);
    }

    // Test seam: the real event log's own PowerCycleVerdict for a resumed run cannot be chosen
    // from a test (RunPowerCycleProbe always reads this machine's own history), so this drives
    // the real ShowNotedStartWarning/ProceedWithNotedStart pair directly with a verdict of its
    // choosing, the way a real StartNoted gate result would have called it.
    internal void ShowNotedStartWarningForTests(string host, DisplayRow row, ResumeInstruction instruction, string warning)
    {
        // The noted-start warning and its button live in the List view (they are shown while
        // starting a specific selected row, before BeginRun ever switches to Running); this test
        // seam calls the real ShowNotedStartWarning directly, sometimes while a different row's run
        // is already active and Running is showing instead, so NotedStartWarningVisibleForTests
        // (which reads Visible through WinForms' own parent-chain fold) needs List showing to read
        // what ShowNotedStartWarning itself actually set.
        SwitchToView(MainView.List);
        ShowNotedStartWarning(host, row, instruction, warning);
    }

    internal bool NotedStartWarningVisibleForTests => _notedStartWarningLabel.Visible && _notedStartButton.Visible;

    internal string NotedStartWarningTextForTests => _notedStartWarningLabel.Text;

    internal void ClickNotedStartButtonForTests()
    {
        SwitchToView(MainView.List);
        _notedStartButton.PerformClick();
    }

    internal string StartButtonTextForTests => _startButton.Text;

    // Test seam: the row list's own "State" column (SubItems[1]; SubItems[0] is always the same
    // text as the ListViewItem's own Number), read the same way RowStateSymbol's own text: the
    // sub-item's Tag, which PopulateRows sets to the plain/technical state text alone, without the
    // small symbol prefixed onto the cell's own displayed Text.
    internal string? RowStateTextForTests(string number)
    {
        for (int i = 0; i < _displayRows.Count; i++)
        {
            if (_displayRows[i].Number == number)
            {
                return _rowList.Items[i].SubItems[1].Tag as string ?? _rowList.Items[i].SubItems[1].Text;
            }
        }

        return null;
    }

    // Selecting a row is a List-view action in the real window ("Choose one test"), so this test
    // seam switches there first: a real click could never reach the row list otherwise, since
    // WinForms' own Visible getter folds a hidden parent (Home showing instead) into every child's
    // own Visible read, and this keeps every existing test's own SelectRowForTests-then-Click*
    // pattern working unchanged.
    internal bool SelectRowForTests(string number)
    {
        SwitchToView(MainView.List);
        for (int i = 0; i < _displayRows.Count; i++)
        {
            if (_displayRows[i].Number == number)
            {
                _rowList.Items[i].Selected = true;
                UpdateStartButton();
                UpdateRowDetail();
                return true;
            }
        }

        return false;
    }

    internal void ClickStartForTests()
    {
        SwitchToView(MainView.List);
        _startButton.PerformClick();
    }

    internal void ClickStopForTests() => _stopButton.PerformClick();

    internal void ClickRunAllForTests()
    {
        SwitchToView(MainView.Home);
        _runAllButton.PerformClick();
    }

    // Test seam: the real 20 s grace period OnRunFinished waits for a child to exit on its own
    // after sending its own exit message, shortened here so a test proving the orphan-cleanup
    // path does not have to wait 20 real seconds for it.
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal TimeSpan ExitGracePeriodForTests { get; set; } = TimeSpan.FromSeconds(20);

    // Test seam: drives the real BeginRun/OnRunFinished pipeline for a runner and script built
    // entirely by the test (not one of the 16 shipped scripts), so the orphan-on-missed-exit path
    // can be proved against a child that deliberately outlives its own exit message.
    internal void BeginRunForTests(DisplayRow row, ChildRunner runner, string resultFolder) =>
        BeginRun(row, runner, resultFolder, isResume: false);

    // Test seam: drives the real BeginRun/OnRunFinished/OnRunAllRestoreRecoveryFinished pipeline
    // for a runner and script built entirely by the test, so the red-banner recovery outcome (a
    // Restore that does or does not record leftAtRest yes) can be proved against a controlled
    // result.json without needing the real 00-Restore.ps1 against a real or sandboxed device.
    internal void BeginRunAllRestoreRecoveryForTests(ChildRunner runner, string resultFolder)
    {
        _runAllActive = true;
        _runAllRecoveringViaRestore = true;
        DisplayRow restoreRow = _displayRows.First(r => r.Row.Number == "00");
        BeginRun(restoreRow, runner, resultFolder, isResume: false);
    }

    internal string? ActiveRowNumberForTests => _activeDisplayRow?.Number;

    internal bool RunAllActiveForTests => _runAllActive;

    internal void ClickCarryOnForTests()
    {
        SwitchToView(MainView.Running);
        _runAllCarryOnButton.PerformClick();
    }

    internal void KillActiveRunForTests() => KillActiveRun();

    internal string StatusTextForTests => _statusLabel.Text;

    internal string ExePathForTests => _exePath;

    internal string ExePathLabelTextForTests => _exePathLabel.Text;

    internal void ClickChooseExeButtonForTests()
    {
        EnsureHomeMoreExpandedForTests();
        _chooseExeButton.PerformClick();
    }

    internal string RunAllStatusTextForTests => _runAllStatusLabel.Text;

    internal bool CarryOnVisibleForTests => _runAllCarryOnButton.Visible;

    internal void ClickRunAllStopHereForTests()
    {
        SwitchToView(MainView.Running);
        _runAllStopHereButton.PerformClick();
    }

    internal bool RunAllStopHereVisibleForTests => _runAllStopHereButton.Visible;

    internal bool BackToStartVisibleForTests => _backToStartButton.Visible;

    internal void ClickBackToStartForTests()
    {
        SwitchToView(MainView.Running);
        _backToStartButton.PerformClick();
    }

    internal bool RunAllRestoreAdviceVisibleForTests => _runAllRestoreAdviceLabel.Visible && _runAllRestoreContinueButton.Visible;

    internal string RunAllRestoreAdviceTextForTests => _runAllRestoreAdviceLabel.Text;

    internal void ClickRunAllRestoreContinueForTests()
    {
        SwitchToView(MainView.Running);
        _runAllRestoreContinueButton.PerformClick();
    }

    internal string RunAllProgressTextForTests => _runAllProgressLabel.Text;

    internal bool RunAllProgressVisibleForTests => _runAllProgressLabel.Visible;

    internal string RunAllButtonTextForTests => _runAllButton.Text;

    internal bool StartButtonEnabledForTests => _startButton.Enabled;

    internal bool RunAllButtonEnabledForTests => _runAllButton.Enabled;

    // Test seam: Run all's own button is legitimately disabled for as long as a row's Start is
    // active (BeginRun), so a real click cannot reach StartOrContinueRunAll then; PerformClick is
    // a no-op on a disabled control. A test proving the handler itself still refuses a second
    // child, not only that the button happened to be greyed out, forces Enabled true first.
    internal void ForceRunAllButtonEnabledForTests() => _runAllButton.Enabled = true;

    internal int SelectedIndexForTests => _rowList.SelectedIndices.Count > 0 ? _rowList.SelectedIndices[0] : -1;

    internal int SelectedIndexCountForTests => _rowList.SelectedIndices.Count;

    // Test seam: marks a second item Selected without first clearing the row list's own
    // selection, the way a real ctrl or shift click would leave it if MultiSelect ever let one
    // through; a single-select ListView (real handle, real control) clears the earlier item on its
    // own the moment this runs, never this seam's job to enforce.
    internal void SelectAdditionalRowForTests(string number)
    {
        for (int i = 0; i < _displayRows.Count; i++)
        {
            if (_displayRows[i].Number == number)
            {
                _rowList.Items[i].Selected = true;
                return;
            }
        }
    }

    // Control.CreateControl() (protected on every Control) silently does nothing while Visible
    // is false, which a Form always is until shown; parked off-screen, with no taskbar entry, so
    // nothing is ever seen, but every child control gets a real handle and the real message loop
    // Control.BeginInvoke (every ChildRunner event) depends on, the same as actually running it.
    internal void ForceControlCreationForTests()
    {
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        ShowInTaskbar = false;
        Show();
    }
}
