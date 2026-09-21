using Earshot.TestWindow.Core;

namespace Earshot.TestWindow.Ui;

// The window's shell. A row is picked from the list, Start begins a real (or, with --sandbox,
// fake-device) child, StepPanel shows every prompt, ResultPanel shows what result.json says
// once it exits. Test 10 is flattened into its five variant rows (DisplayRow.Flatten), so every
// one of the 16 tests and all 22 halves is its own clickable entry; none is a text box.
internal sealed class MainForm : Form
{
    private readonly string _repoRoot;
    private readonly IReadOnlyList<ManifestRow> _rows;
    private readonly IReadOnlyList<DisplayRow> _displayRows;
    private readonly IReadOnlyList<WordingEntry> _wording;
    private readonly SandboxOptions? _sandbox;
    private readonly string _exePath;

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
    private readonly Button _runAllCarryOnButton;
    private readonly System.Windows.Forms.Timer _watchdogTimer;

    // The administrator prompt check's own control, separate from the row list (it is a
    // utility check, not one of the 16 numbered tests, and never appears in Run all's own order).
    private readonly Button _rehearsalButton;
    private readonly Label _rehearsalWarningLabel;
    private readonly Label _rehearsalStatusLabel;
    private readonly DisplayRow _rehearsalRow;

    private readonly List<Exception> _postDisposalDeliveryFailures = new();

    private ChildRunner? _activeRunner;
    private string? _activeResultFolder;
    private bool _activeIsResume;
    private TestRowSpec? _activeSpec;
    private DisplayRow? _activeDisplayRow;
    private BannerState _banner = new() { Level = BannerLevel.None };
    private readonly List<string> _transcript = new();

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

    internal MainForm(string repoRoot, IReadOnlyList<ManifestRow> rows, IReadOnlyList<WordingEntry> wording, SandboxOptions? sandbox, string exePath)
    {
        _repoRoot = repoRoot;
        _rows = rows;
        _displayRows = DisplayRow.Flatten(rows);
        _wording = wording;
        _sandbox = sandbox;
        _exePath = exePath;

        Text = "Earshot live tests" + (sandbox is not null ? " (SANDBOX, no device)" : string.Empty);
        Width = 1040;
        Height = 720;
        StartPosition = FormStartPosition.CenterScreen;

        // A row shows a plain name and one line saying what the test proves, plus its state, not
        // the TestId alone. State is the second column and every column is sized to fit inside
        // leftPanel's own fixed width, so the state is always on screen at the default window
        // size, never behind a horizontal scroll; the full text of a long name or line is always
        // available in full underneath, in _rowDetailLabel.
        _rowList = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false,
        };
        _rowList.Columns.Add("#", 30);
        _rowList.Columns.Add("State", 110);
        _rowList.Columns.Add("Test", 130);
        _rowList.Columns.Add("What it proves", 160);
        _rowList.SelectedIndexChanged += (_, _) => { UpdateStartButton(); UpdateRowDetail(); };

        _rowDetailLabel = new Label { Dock = DockStyle.Bottom, Height = 110, AutoEllipsis = false, TextAlign = ContentAlignment.TopLeft };

        _speakerChoiceLabel = new Label
        {
            Dock = DockStyle.Bottom, Height = 20, TextAlign = ContentAlignment.MiddleLeft, ForeColor = SystemColors.GrayText,
            Text = Copy.SpeakerAddressChoicePrompt, Visible = false,
        };
        _speakerChoiceRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, FlowDirection = FlowDirection.LeftToRight, Visible = false };

        var leftPanel = new Panel { Dock = DockStyle.Left, Width = 460 };
        leftPanel.Controls.Add(_rowList);
        leftPanel.Controls.Add(_speakerChoiceRow);
        leftPanel.Controls.Add(_speakerChoiceLabel);
        leftPanel.Controls.Add(_rowDetailLabel);

        _startButton = new Button { Text = "Start", Dock = DockStyle.Top, Height = 32 };
        _startButton.Click += (_, _) => StartSelectedRow();

        _notedStartWarningLabel = new Label
        {
            Dock = DockStyle.Top, Height = 48, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DarkRed, Visible = false,
        };
        _notedStartButton = new Button { Text = "Start anyway, noted", Dock = DockStyle.Top, Height = 28, Visible = false };
        _notedStartButton.Click += (_, _) => ProceedWithNotedStart();

        // The form only renders the current prompt, the transcript box and a Stop
        // button, standing throughout a run, not only for the rare unrecognised-prompt case
        // StepPanel's own built-in stop button covers.
        _stopButton = new Button { Text = "Stop the test", Dock = DockStyle.Top, Height = 28, Enabled = false };
        _stopButton.Click += (_, _) => OnStopClicked();

        _watchdogTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _watchdogTimer.Tick += (_, _) => OnWatchdogTick();
        _watchdogTimer.Start();

        _caseBox = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, Visible = sandbox is not null };
        _caseBox.Items.AddRange(new object[] { "none", "one", "two" });
        _caseBox.SelectedIndex = 1;

        _statusLabel = new Label { Dock = DockStyle.Top, Height = 28, TextAlign = ContentAlignment.MiddleLeft };
        _bannerLabel = new Label
        {
            Dock = DockStyle.Top, Height = 0, TextAlign = ContentAlignment.MiddleLeft, Visible = false,
            Font = new Font(Font, FontStyle.Bold), AutoEllipsis = false,
        };

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

        _runAllCarryOnButton = new Button { Text = Copy.RunAllCarryOnButtonLabel, Dock = DockStyle.Top, Height = 28, Visible = false, AutoSize = true };
        _runAllCarryOnButton.Click += (_, _) => OnRunAllCarryOnClicked();

        _runAllStatusLabel = new Label { Dock = DockStyle.Top, Height = 24, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DarkSlateBlue };

        _runAllExplanationLabel = new Label
        {
            Dock = DockStyle.Top, Height = 32, TextAlign = ContentAlignment.MiddleLeft, ForeColor = SystemColors.GrayText,
            Text = Copy.RunAllExplanation,
        };

        _runAllButton = new Button { Text = Copy.RunAllButtonLabel, Dock = DockStyle.Top, Height = 28 };
        _runAllButton.Click += (_, _) => StartOrContinueRunAll();

        // Beside row 15 rather than inside the row list, since this is a utility check, not
        // one of the 16 numbered tests. Disabled outright in a sandbox window (never Visible at
        // all is not enough on its own; StartRehearsal itself refuses too, belt and braces): an
        // unattended or development sandbox must never run this, because it always raises a real
        // Windows administrator prompt, in any mode.
        _rehearsalRow = BuildRehearsalDisplayRow();
        _rehearsalWarningLabel = new Label
        {
            Dock = DockStyle.Top, Height = 48, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DarkRed,
            Text = Copy.RehearsalWarning,
        };
        _rehearsalStatusLabel = new Label
        {
            Dock = DockStyle.Top, Height = 20, TextAlign = ContentAlignment.MiddleLeft, ForeColor = SystemColors.GrayText,
            Text = sandbox is null ? string.Empty : Copy.RehearsalNeverInSandbox,
        };
        _rehearsalButton = new Button
        {
            Text = Copy.RehearsalRowName, Dock = DockStyle.Top, Height = 28, Enabled = sandbox is null,
        };
        _rehearsalButton.Click += (_, _) => StartRehearsal();

        var rightPanel = new Panel { Dock = DockStyle.Fill };
        rightPanel.Controls.Add(contentHost);
        rightPanel.Controls.Add(_statusLabel);
        rightPanel.Controls.Add(_runAllStatusLabel);
        rightPanel.Controls.Add(_runAllCarryOnButton);
        rightPanel.Controls.Add(_bannerLabel);
        rightPanel.Controls.Add(_caseBox);
        rightPanel.Controls.Add(_stopButton);
        rightPanel.Controls.Add(_notedStartButton);
        rightPanel.Controls.Add(_notedStartWarningLabel);
        rightPanel.Controls.Add(_startButton);
        rightPanel.Controls.Add(_rehearsalStatusLabel);
        rightPanel.Controls.Add(_rehearsalWarningLabel);
        rightPanel.Controls.Add(_rehearsalButton);
        rightPanel.Controls.Add(_runAllExplanationLabel);
        rightPanel.Controls.Add(_runAllButton);

        Controls.Add(rightPanel);
        Controls.Add(leftPanel);

        FormClosing += OnFormClosing;

        PopulateRows();
        UpdateStartButton();
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

        // The plain line first, the script's own words as a secondary line beneath it, for both
        // pairs Name/Title and Proves/Settles, in full: never cut with an ellipsis.
        string text = row.Name + Environment.NewLine + row.Title + Environment.NewLine + Environment.NewLine +
            row.Proves + Environment.NewLine + "Settles: " + row.Settles;
        if (row.WaitsOnWindowsUpdate)
        {
            text += Environment.NewLine + "This variant waits on Windows Update offering a restart; it may take a while for one to appear.";
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

        if (_sandbox is not null && row.Row.Number == "10" && row.VariantNumber is >= 2 and <= 5)
        {
            text += Environment.NewLine + Environment.NewLine + Copy.Test10VariantNotSandboxTestable;
        }

        if (row.Row.Number == "14")
        {
            IReadOnlyList<string> candidates = SpeakerCandidateFinder.Find(LiveTestRoot());
            if (candidates.Count == 0)
            {
                text += Environment.NewLine + Environment.NewLine + Copy.SpeakerAddressNoCandidates;
            }

            UpdateSpeakerChoice(candidates);
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
    private void UpdateSpeakerChoice(IReadOnlyList<string>? candidates)
    {
        _speakerChoiceRow.Controls.Clear();

        if (candidates is null || candidates.Count == 0)
        {
            _speakerChoiceRow.Visible = false;
            _speakerChoiceLabel.Visible = false;
            _chosenSpeakerAddress = null;
            return;
        }

        if (_chosenSpeakerAddress is not null && !candidates.Contains(_chosenSpeakerAddress, StringComparer.Ordinal))
        {
            _chosenSpeakerAddress = null;
        }

        _speakerChoiceLabel.Visible = true;
        _speakerChoiceRow.Visible = true;

        foreach (string address in candidates)
        {
            string captured = address;
            var button = new Button { Text = address, AutoSize = true, Margin = new Padding(4) };
            button.Click += (_, _) => _chosenSpeakerAddress = captured;
            _speakerChoiceRow.Controls.Add(button);
        }

        var noneButton = new Button { Text = Copy.SpeakerAddressChoiceNone, AutoSize = true, Margin = new Padding(4) };
        noneButton.Click += (_, _) => _chosenSpeakerAddress = null;
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
            DialogResult runChoice = ConfirmDialogForTests(
                "A test is still running. Closing now stops it, the same as Stop the test: no result is written and this PC may not be at rest. Close anyway?");
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

        DialogResult choice = ConfirmDialogForTests("This PC is not at rest. Close anyway?");
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
        _banner = Banner.Compute(LiveTestRoot());
        UpdateBannerLabel();
        UpdateRehearsalStatus();

        int selected = _rowList.SelectedIndices.Count > 0 ? _rowList.SelectedIndices[0] : -1;
        _rowList.Items.Clear();
        foreach (DisplayRow row in _displayRows)
        {
            DerivedRowState state = ComputeState(row);
            var item = new ListViewItem(row.Number);
            item.SubItems.Add(RowPresenter.Text(state));
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

    private void UpdateBannerLabel()
    {
        if (_banner.Level == BannerLevel.None)
        {
            _bannerLabel.Visible = false;
            _bannerLabel.Height = 0;
            return;
        }

        _bannerLabel.Text = _banner.Message;
        _bannerLabel.ForeColor = _banner.Level == BannerLevel.Red ? Color.White : Color.Black;
        _bannerLabel.BackColor = _banner.Level == BannerLevel.Red ? Color.Firebrick : Color.Goldenrod;
        _bannerLabel.Height = 32;
        _bannerLabel.Visible = true;
    }

    private DerivedRowState ComputeState(DisplayRow row)
    {
        if (IsElevationLocked(row))
        {
            return new DerivedRowState { Kind = RowStateKind.Locked, Reason = Copy.LockedDetail };
        }

        TestRowSpec spec = row.ToSpec();
        IReadOnlyList<RunEvidence> evidence = EvidenceStore.LoadEvidence(LiveTestRoot(), row.TestId);
        DateTimeOffset? exeWrite = File.Exists(_exePath) ? File.GetLastWriteTimeUtc(_exePath) : null;
        return StateDeriver.Derive(spec, evidence, _exePath, exeWrite);
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

        if (_activeRunner is not null || _banner.RowsLockedExceptRestore)
        {
            return;
        }

        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            _statusLabel.Text = "Windows PowerShell 5.1 is not installed at " + host + ".";
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
        row.Halves == 2 ? PendingRunFinder.Find(row.ToSpec(), LiveTestRoot()) : null;

    private void UpdateStartButton()
    {
        int index = _rowList.SelectedIndices.Count > 0 ? _rowList.SelectedIndices[0] : -1;
        if (index < 0 || index >= _displayRows.Count)
        {
            _startButton.Enabled = false;
            _startButton.Text = "Start";
            return;
        }

        DisplayRow row = _displayRows[index];
        // The red banner locks every row except 00 Restore.
        bool lockedByBanner = _banner.RowsLockedExceptRestore && row.Row.Number != "00";

        PendingRun? pending = FindPendingRun(row);
        _startButton.Text = pending is not null ? "Carry on with the second half" : "Start";
        _startButton.Enabled = _activeRunner is null && !lockedByBanner && !IsElevationLocked(row);
    }

    private void StartSelectedRow()
    {
        int index = _rowList.SelectedIndices.Count > 0 ? _rowList.SelectedIndices[0] : -1;
        if (index < 0 || index >= _displayRows.Count)
        {
            return;
        }

        // Exactly one child may exist. Checked here, not only via the Start button's own
        // Enabled state, because Run all and its carry-on button call this same start path
        // programmatically, never through a click a disabled button could have blocked.
        if (!RunGate.CanStart(_activeRunner))
        {
            return;
        }

        DisplayRow row = _displayRows[index];
        if (_banner.RowsLockedExceptRestore && row.Row.Number != "00")
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
            _statusLabel.Text = "Windows PowerShell 5.1 is not installed at " + host + ".";
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
            _statusLabel.Text = "resume.txt could not be used, so nothing was started: " + reason;
            return;
        }

        // "since" is the first-half snapshot's finishedUtc, else resume.txt's last
        // write time.
        DateTimeOffset since = FirstHalfSnapshotFinishedUtc(pending.Folder, row.TestId) ?? File.GetLastWriteTimeUtc(resumeTxtPath);
        PowerCycleVerdict verdict;
        if (PowerCycleVerdictOverrideForTests is { } overrideVerdict)
        {
            verdict = overrideVerdict();
            PowerCycleEvidenceFile.Write(pending.Folder, "{}", verdict);
        }
        else
        {
            string probeScript = Path.Combine(_repoRoot, "tools", "live-tests", "gui", "Get-PowerCycleEvidence.ps1");
            string evidenceJson = ChildRunner.RunPowerCycleProbe(host, probeScript, since, TimeSpan.FromSeconds(30));
            verdict = PowerCycle.Decide(evidenceJson);
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
            ShowNotedStartWarning(host, row, instruction!, PowerCycleGate.NotedWarning(row.PowerCycleRequirement, verdict));
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
        _statusLabel.Text = "This start needs a deliberate click before it counts as tried.";
    }

    private void ProceedWithNotedStart()
    {
        if (_pendingNotedStart is not { } pending)
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

    private void BeginRun(DisplayRow row, ChildRunner runner, string resultFolder, bool isResume)
    {
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
                MarkUnknownAndReset("The test process ended without ever reporting a result. This row now reads Unknown until Restore has run.");
            }
        });

        _resultPanel.Visible = false;
        _handOffBox.Visible = false;
        _stepPanel.Visible = true;
        _statusLabel.Text = "Running " + row.TestId + (isResume ? " (second half)..." : "...");
        UpdateStartButton();
        runner.Start();
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
                _stepPanel.Show(_activeRunner!, presented, message.Seq);
                break;
            case ChildMessageKind.Exit:
                _statusLabel.Text = row.TestId + " finished (exit " + message.ExitCode + ").";
                OnRunFinished(row);
                break;
            case ChildMessageKind.Crash:
                _statusLabel.Text = row.TestId + " crashed: " + message.Text;
                OnRunFinished(row);
                break;
            case ChildMessageKind.Unreadable:
                _statusLabel.Text = "Unreadable message: " + message.Text;
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

        string resultPath = Path.Combine(_activeResultFolder!, "result.json");
        (ParsedResult? result, string? failure) = EvidenceStore.TryReadResult(resultPath, row.TestId);

        // A declined start (No at Show-Preconditions, or any other stop before the script
        // ever reaches the point of writing resume.txt) still produces a readable result.json, but
        // there is nothing pending to come back to. The pending detection elsewhere already
        // requires resume.txt; the hand-off screen ("Now shut this PC down... it will pick up
        // here") must ask for exactly the same thing, not show it whenever this happened to be a
        // two-half test's first half regardless of what the run actually reached.
        bool wasFirstHalfOfTwoHalfTest = row.Halves == 2 && !_activeIsResume &&
            File.Exists(Path.Combine(_activeResultFolder!, "resume.txt"));
        if (result is not null && wasFirstHalfOfTwoHalfTest)
        {
            ShowHandOff(row, result);
        }
        else if (result is not null)
        {
            _resultPanel.Show(ResultPresenter.Present(result, _activeResultFolder!));
            _resultPanel.Visible = true;
        }
        else
        {
            _statusLabel.Text = "No readable result.json: " + failure;
        }

        _activeRunner = null;
        _activeIsResume = false;
        _activeSpec = null;
        _activeDisplayRow = null;
        _currentPromptSeq = null;
        _killDeadlineUtc = null;
        _stopButton.Enabled = false;
        _runAllButton.Enabled = true;
        PopulateRows();
        UpdateStartButton();

        // The halt is decided from result.json, never from the click. Whatever
        // just finished, Run all (if active) re-derives this same item from disk and decides
        // afresh whether to stop here or move itself on; it never trusts what this method above
        // just did with the panels.
        if (_runAllActive)
        {
            AdvanceRunAll();
        }
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
            _statusLabel.Text = "Stop sent. Waiting up to 60 s for the test to finish on its own.";
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
                _statusLabel.Text = "This test has been silent for " + Math.Max(1, maxSilenceSeconds / 60) +
                    " minutes. Keep waiting, or Stop the test.";
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
            "Stopping it now means no result is written and this PC may not be at rest.",
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

        MarkUnknownAndReset("Stopped by force. This row now reads Unknown until Restore has run.");
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
        _runAllButton.Enabled = true;
        PopulateRows();
        UpdateStartButton();

        // A forced kill, or a process ending on its own with nothing reported, is never a
        // script-decided outcome: Run all treats either one exactly like an ordinary halt on
        // failure, never advanced past on its own.
        if (_runAllActive && row is not null)
        {
            HaltRunAll(row, new DerivedRowState { Kind = RowStateKind.Unknown, Reason = "stopped by force" });
        }
    }

    // Run all's guided sequence. Walks RunAllOrder from _runAllIndex; test 10's five
    // variants are ordinary items here (each is its own DisplayRow with its own RunAllKey), never
    // skipped. It starts at most one child per call, then returns and waits for OnRunFinished to
    // call back in; it never loops past a row that is not yet a clean pass on disk.
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
            // clicking "Run all, step by step" again resumes here rather than from the start.
            SaveRunAllProgress();

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
        _runAllStatusLabel.Text = Copy.RunAllFinished;
    }

    private void HaltRunAll(DisplayRow row, DerivedRowState state)
    {
        SaveRunAllProgress();
        SelectRow(row);

        bool atPowerCycleBoundary = state.Kind is RowStateKind.WaitingForShutDown or RowStateKind.WaitingForRestart;
        _runAllStatusLabel.Text = state.Kind == RowStateKind.Locked
            ? Copy.RunAllLockedItemSkipped + " " + Copy.LockedDetail
            : atPowerCycleBoundary
                ? Copy.RunAllStoppedForPowerCycle(row.Number)
                : Copy.RunAllStoppedForFailure(row.Number);

        // At the power-cycle boundary the row's own "Carry on with the second half" button
        // is the deliberate click that resumes Run all too, through
        // OnRunFinished -> AdvanceRunAll above; an ordinary failure needs its own
        // acknowledgement first: until it is pressed, Run all stays halted.
        _runAllCarryOnButton.Visible = !atPowerCycleBoundary;
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
        _runAllIndex++;
        SaveRunAllProgress();
        AdvanceRunAll();
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

    private void StartOrContinueRunAll()
    {
        // Run all's own button stayed enabled during a run; nothing stopped a second click
        // (or a click while a single-row Start was mid-flight) from starting a second child.
        if (!RunGate.CanStart(_activeRunner))
        {
            return;
        }

        if (_banner.RowsLockedExceptRestore)
        {
            _runAllStatusLabel.Text = _banner.Message;
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
        AdvanceRunAll();
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
        _statusLabel.Text = row.TestId + ": first half complete. Waiting for the power cycle.";
    }

    // The one call site every ChildRunner.MessageReceived and TranscriptLine closure must go
    // through, never a raw BeginInvoke. ReadLoop runs on its own ThreadPool thread and can still
    // be delivering a message the instant this form's handle is destroyed (Dispose, or a test
    // harness's own teardown right after it): a plain IsHandleCreated read is not enough, because
    // it can pass and then go stale before BeginInvoke actually runs, and a check on one thread
    // followed by a call on another cannot be made atomic against a Dispose happening in between.
    // The try/catch below is what is actually safe under that race. Anything it catches is
    // recorded, twice, never swallowed: PostDisposalDeliveryFailuresForTests for a test to read,
    // and a trace line for anyone reading this process's own diagnostic output, because an
    // uncaught exception here is unhandled on a background thread by construction and takes the
    // whole process down with it.
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
        _postDisposalDeliveryFailures.Add(ex);
        System.Diagnostics.Trace.TraceWarning("A message delivery from ChildRunner arrived after this window's handle was gone: " + ex);
    }

    // Test seams only (Earshot.Tests, via InternalsVisibleTo): a fix whose only proof is an
    // extracted predicate in isolation proves nothing about the caller that used to bypass it.
    // These let a test drive this form's real click handlers headlessly (constructed, handle
    // forced, never Shown) and observe what a real click actually does, the same way this form's
    // own private methods are wired to controls.
    internal ChildRunner? ActiveRunnerForTests => _activeRunner;

    internal void SafeBeginInvokeForTests(Action action) => SafeBeginInvoke(action);

    internal IReadOnlyList<Exception> PostDisposalDeliveryFailuresForTests => _postDisposalDeliveryFailures;

    // M9 test seams: drives the real silence watchdog tick and lets a test move "the last
    // activity was seen" into the past without a real wait, so both directions (fires on real
    // silence; never fires while a prompt is pending, however long) are provable in milliseconds.
    internal void ForceWatchdogTickForTests() => OnWatchdogTick();

    internal void SetLastActivityUtcForTests(DateTimeOffset utc) => _lastActivityUtc = utc;

    internal int? CurrentPromptSeqForTests => _currentPromptSeq;

    internal bool HasKillDeadlineForTests => _killDeadlineUtc is not null;

    internal void ClickCurrentPromptButtonForTests() => _stepPanel.ClickFirstButtonForTests();

    internal void ClickPromptButtonForTests(int index) => _stepPanel.ClickButtonForTests(index);

    internal FormClosingEventArgs RaiseFormClosingForTests(CloseReason reason)
    {
        var args = new FormClosingEventArgs(reason, cancel: false);
        OnFormClosing(this, args);
        return args;
    }

    // M4 test seams. ClickRehearsalButtonForTests uses PerformClick (bypasses Enabled, the same
    // as every other *ForTests click), so it proves StartRehearsal's own runtime sandbox guard
    // independently of the Enabled=false set at construction: even a stray click while sandboxed
    // must never start anything.
    internal bool RehearsalButtonEnabledForTests => _rehearsalButton.Enabled;

    internal string RowDetailTextForTests => _rowDetailLabel.Text;

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

    internal void ClickRehearsalButtonForTests() => _rehearsalButton.PerformClick();

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
    internal void ShowNotedStartWarningForTests(string host, DisplayRow row, ResumeInstruction instruction, string warning) =>
        ShowNotedStartWarning(host, row, instruction, warning);

    internal bool NotedStartWarningVisibleForTests => _notedStartWarningLabel.Visible && _notedStartButton.Visible;

    internal string NotedStartWarningTextForTests => _notedStartWarningLabel.Text;

    internal void ClickNotedStartButtonForTests() => _notedStartButton.PerformClick();

    internal bool SelectRowForTests(string number)
    {
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

    internal void ClickStartForTests() => _startButton.PerformClick();

    internal void ClickStopForTests() => _stopButton.PerformClick();

    internal void ClickRunAllForTests() => _runAllButton.PerformClick();

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

    internal void ClickCarryOnForTests() => _runAllCarryOnButton.PerformClick();

    internal void KillActiveRunForTests() => KillActiveRun();

    internal string StatusTextForTests => _statusLabel.Text;

    internal string RunAllStatusTextForTests => _runAllStatusLabel.Text;

    internal bool CarryOnVisibleForTests => _runAllCarryOnButton.Visible;

    internal bool StartButtonEnabledForTests => _startButton.Enabled;

    internal int SelectedIndexForTests => _rowList.SelectedIndices.Count > 0 ? _rowList.SelectedIndices[0] : -1;

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
