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

    // M4: the administrator prompt check's own control, separate from the row list (it is a
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

    // section 8.3: the silence watchdog and the abort/kill sequence. A prompt on screen is never
    // a hang (test 12 waits hours at one), so the watchdog only ever looks at silence while
    // _currentPromptSeq is null. _killDeadlineUtc is set once, either by an abort waiting for a
    // natural exit or (implicitly, by going straight to the confirmation) when Stop is clicked
    // with nothing pending.
    private int? _currentPromptSeq;
    private DateTimeOffset _lastActivityUtc;
    private DateTimeOffset? _killDeadlineUtc;
    private bool _silenceWarningShown;

    // section 11: Run all's own state, held only in memory plus run-all.json; never consulted by
    // StateDeriver, so a row's own state is always exactly what section 6.2 says regardless of
    // whether Run all is active.
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

        var leftPanel = new Panel { Dock = DockStyle.Left, Width = 460 };
        leftPanel.Controls.Add(_rowList);
        leftPanel.Controls.Add(_rowDetailLabel);

        _startButton = new Button { Text = "Start", Dock = DockStyle.Top, Height = 32 };
        _startButton.Click += (_, _) => StartSelectedRow();

        // section 8.2: "the form only renders the current prompt, the transcript box and a Stop
        // button", standing throughout a run, not only for the rare unrecognised-prompt case
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
        // M9: the silence watchdog must stop treating a reply as though the prompt it answered
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

        // M4: beside row 15 rather than inside the row list, since this is a utility check, not
        // one of the 16 numbered tests. Disabled outright in a sandbox window (never Visible at
        // all is not enough on its own; StartRehearsal itself refuses too, section 8.1's own
        // belt-and-braces): an unattended or development sandbox must never run this, because it
        // always raises a real Windows administrator prompt, in any mode.
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
            return;
        }

        int index = _rowList.SelectedIndices[0];
        if (index < 0 || index >= _displayRows.Count)
        {
            _rowDetailLabel.Text = string.Empty;
            return;
        }

        DisplayRow row = _displayRows[index];

        // The plain line first, the script's own words as a secondary line beneath it, for both
        // pairs Name/Title and Proves/Settles, in full: never cut with an ellipsis.
        string text = row.Name + Environment.NewLine + row.Title + Environment.NewLine + Environment.NewLine +
            row.Proves + Environment.NewLine + "Settles: " + row.Settles;
        if (row.WaitsOnWindowsUpdate)
        {
            text += Environment.NewLine + "This variant waits on Windows Update offering a restart; it may take a while for one to appear.";
        }

        // M13: "the brief forbids a silently missing path." Restore's own uninstall offer and
        // 07's plan B are real branches these scripts declare (-OfferUninstall, -AllowPlanB) that
        // this window never passes as true anywhere, so this row can never reach them; said here
        // rather than left for the owner to notice on his own.
        if (row.Number == "00")
        {
            text += Environment.NewLine + Environment.NewLine + Copy.RestoreUninstallOfferNotAvailable;
        }
        else if (row.Number == "07")
        {
            text += Environment.NewLine + Environment.NewLine + Copy.PlanBNotAvailable;
        }

        _rowDetailLabel.Text = text;
    }

    // design.md section 4.7: closing while the banner is red asks first. section 8.3/S8: closing
    // during a run asks first too, and closing anyway is the same as a forced kill (the process
    // is going away either way; the row must read Unknown afterwards, not whatever it said
    // before this run started).
    // Test seam: real callers never replace this; it defaults to the real modal box every
    // OnFormClosing decision used to call directly. Substituting it in a test proves what
    // OnFormClosing decided to do without ever putting a real dialog on screen, which nothing
    // here can safely dismiss on its own.
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<string, DialogResult> ConfirmDialogForTests { get; set; } =
        message => MessageBox.Show(message, "Earshot live tests", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

    // M10, design.md section 4.4: "Windows ends the session while a half is running: the form
    // cancels the close once, so Windows shows its own 'this app is preventing shut down' screen
    // with the window title 'Earshot live tests: a test is still running'. If the owner forces
    // it, the child dies with the session and the next open shows the red banner." Windows sends
    // WM_QUERYENDSESSION and waits for FormClosing to answer synchronously; a modal MessageBox
    // does not answer it, it blocks the answer, so this path never shows one. The cancel happens
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

    private void PopulateRows()
    {
        // Recomputed at every open and after every half (design.md section 4.6).
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

    // M4: a hand-built row for the administrator prompt check, never one of the 16 in
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

    // M4: never in a sandbox window (an unattended or development sandbox must never raise a
    // real Windows administrator prompt), never while another run is active (RunGate's own rule,
    // section 8.2), and always through the production driver: D8 and section 10.2 are explicit
    // that nothing about this site's own execution may be faked.
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

    // section 10.2's lock rule, wired to row 15: its whole test is the elevated uninstall/install
    // cycle, so it is unambiguous. M13: 00's uninstall variant and 07's plan B are the other two
    // rows the spec names; this round does not give either its own locked row (the safer of the
    // spec's two sanctioned options was taken instead: UpdateRowDetail's own note on rows 00 and
    // 07 says plainly what is not offered and why), so neither is gated here either.
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
    // been set aside (section 9.2). Because each variant carries its own TestId, this can never
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
        // The red banner locks every row except 00 Restore (design.md section 4.6).
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

        // B1: exactly one child may exist. Checked here, not only via the Start button's own
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
                host, driver, scriptPath, _exePath, runRoot, resume: false, variant: row.VariantNumber, offerUninstall: false, allowPlanB: false,
                environmentOverrides: _sandbox.ChildEnvironment, extraArguments: extra);
        }
        else
        {
            string driver = Path.Combine(_repoRoot, "tools", "live-tests", "gui", "Invoke-GuiHalf.ps1");
            runner = new ChildRunner(host, driver, scriptPath, _exePath, runRoot, resume: false, variant: row.VariantNumber, offerUninstall: false, allowPlanB: false);
        }

        BeginRun(row, runner, Path.Combine(runRoot, row.TestId), isResume: false);
    }

    // section 9.2: "A resumed run always uses the pending run's -RunRoot; the window never makes
    // a new root for a second half." resume.txt is parsed by ResumeFile, never executed; only its
    // four validated values (script, exe, root, variant) are ever used to start anything.
    private void StartResumedSecondHalf(string host, DisplayRow row, PendingRun pending)
    {
        string liveTestRoot = LiveTestRoot();
        string resumeTxtPath = Path.Combine(pending.Folder, "resume.txt");
        string[] knownScripts = _rows.Select(r => r.Script).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        if (!ResumeFile.TryParse(resumeTxtPath, _repoRoot, liveTestRoot, knownScripts, out ResumeInstruction? instruction, out string? reason))
        {
            _statusLabel.Text = "resume.txt could not be used, so nothing was started: " + reason;
            return;
        }

        // section 9.3: "since" is the first-half snapshot's finishedUtc, else resume.txt's last
        // write time.
        DateTimeOffset since = FirstHalfSnapshotFinishedUtc(pending.Folder, row.TestId) ?? File.GetLastWriteTimeUtc(resumeTxtPath);
        string probeScript = Path.Combine(_repoRoot, "tools", "live-tests", "gui", "Get-PowerCycleEvidence.ps1");
        string evidenceJson = ChildRunner.RunPowerCycleProbe(host, probeScript, since, TimeSpan.FromSeconds(30));
        PowerCycleVerdict verdict = PowerCycle.Decide(evidenceJson);
        PowerCycleEvidenceFile.Write(pending.Folder, evidenceJson, verdict);

        PowerCycleGateResult gate = PowerCycleGate.Evaluate(row.PowerCycleRequirement, verdict);
        if (gate == PowerCycleGateResult.Refuse)
        {
            _statusLabel.Text = PowerCycleGate.RefusalMessage(row.PowerCycleRequirement, verdict);
            PopulateRows();
            UpdateStartButton();
            return;
        }

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
                host, driver, instruction!.ScriptPath, instruction.ExePath, instruction.RunRoot, resume: true,
                variant: instruction.Variant ?? 0, offerUninstall: false, allowPlanB: false,
                environmentOverrides: _sandbox.ChildEnvironment, extraArguments: extra);
        }
        else
        {
            string driver = Path.Combine(_repoRoot, "tools", "live-tests", "gui", "Invoke-GuiHalf.ps1");
            runner = new ChildRunner(
                host, driver, instruction!.ScriptPath, instruction.ExePath, instruction.RunRoot, resume: true,
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
            // section 11 asks run-all.json to keep for this item, keyed the same as
            // RunAllOrder's own item (RunAllKey, e.g. "10v3").
            string? stamp = Path.GetFileName(Path.GetDirectoryName(resultFolder));
            if (stamp is not null)
            {
                _runAllPointers[row.RunAllKey] = stamp;
            }
        }

        // B1: routed by identity, on the UI thread, at the moment each is actually handled, not
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
                // section 8.3: "a prompt on screen is never a hang." While one is pending the
                // silence watchdog has nothing to say, and Stop's own behaviour changes: it can
                // abort this exact seq rather than only wait-then-kill.
                _currentPromptSeq = message.Seq;
                _silenceWarningShown = false;
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
        _activeRunner?.WaitForExit(TimeSpan.FromSeconds(20));

        string resultPath = Path.Combine(_activeResultFolder!, "result.json");
        (ParsedResult? result, string? failure) = EvidenceStore.TryReadResult(resultPath, row.TestId);

        // M7: a declined start (No at Show-Preconditions, or any other stop before the script
        // ever reaches the point of writing resume.txt) still produces a readable result.json, but
        // there is nothing pending to come back to. Section 9.2's own pending detection already
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

        // section 12: "The halt is decided from result.json, never from the click." Whatever
        // just finished, Run all (if active) re-derives this same item from disk and decides
        // afresh whether to stop here or move itself on; it never trusts what this method above
        // just did with the panels.
        if (_runAllActive)
        {
            AdvanceRunAll();
        }
    }

    // section 8.3: Stop's own two paths. A prompt pending: send the real abort down the wire and
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

        // section 8.3: "no prompt pending and no stdout line for MaxSilenceSeconds. The window
        // then asks, and does nothing by itself." Shown once per silent stretch, on the status
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

    // section 8.3: "kill the process tree... The row is Unknown and the red banner requires
    // Restore before anything else." gui-killed.txt is what makes the row read Unknown rather than
    // falling back to an older, now-untrustworthy pass (StateDeriver.Derive's own newest-run
    // check). M1: it also makes PendingRunFinder stop offering "Carry on" over the stale first-half
    // result a killed second half leaves behind, and makes Banner.Compute distrust that same stale
    // result rather than reading whatever leftAtRest it happens to carry as though it settled
    // anything; a run folder with no result.json at all was already red on its own, but this one
    // does have a (stale, untrustworthy) result.json, which needed its own check in both places.
    private void KillActiveRun()
    {
        if (_activeRunner is null)
        {
            return;
        }

        DisplayRow? row = _activeDisplayRow;
        ChildRunner runner = _activeRunner;
        runner.Kill();

        // Priority-zero fix: Kill() only asks the OS to terminate the process tree; ReadLoop's
        // ReadLine keeps running on its own ThreadPool thread until the killed process's stdout
        // pipe actually closes, a short time later, not synchronously with Kill() returning. This
        // method is called from OnFormClosing (a real close while a half is running) as well as
        // from tests that dispose the form right after calling it (MainFormTestHarness's own
        // cleanup): either way, once this method returns, the form may be disposed at any moment.
        // Draining the read loop here, before that can happen, is what SafeBeginInvoke's disposal
        // guard below is the last line of defence for, not the primary fix.
        try
        {
            runner.WaitForReadLoopAsync().Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException ex)
        {
            _postDisposalDeliveryFailures.Add(ex);
        }

        if (_activeResultFolder is not null)
        {
            Directory.CreateDirectory(_activeResultFolder);
            File.WriteAllText(Path.Combine(_activeResultFolder, "gui-killed.txt"), DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        }

        _stepPanel.Visible = false;
        _resultPanel.Visible = false;
        _handOffBox.Visible = false;
        _statusLabel.Text = "Stopped by force. This row now reads Unknown until Restore has run.";

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

        // A forced kill is the owner overriding the sequence, not a script-decided outcome: Run
        // all treats it exactly like an ordinary halt on failure (section 12), never advanced
        // past on its own.
        if (_runAllActive && row is not null)
        {
            HaltRunAll(row, new DerivedRowState { Kind = RowStateKind.Unknown, Reason = "stopped by force" });
        }
    }

    // section 11: the guided sequence. Walks RunAllOrder from _runAllIndex; test 10's five
    // variants are ordinary items here (each is its own DisplayRow with its own RunAllKey), never
    // skipped. It starts at most one child per call, then returns and waits for OnRunFinished to
    // call back in; it never loops past a row that is not yet a clean pass on disk.
    private void AdvanceRunAll()
    {
        // B1: never start a second child. AdvanceRunAll's only job is to start the next item, so
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

            // B2: the caller's own decision, not just RunAllHalt.ShouldHalt in isolation. A
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
            // clicking "Run all, step by step" again resumes here rather than from the start
            // (T14: "resumes from pointers").
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
        RunAllFile.Delete(LiveTestRoot());
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
        // (section 9.2) is the deliberate click that resumes Run all too, through
        // OnRunFinished -> AdvanceRunAll above; an ordinary failure needs its own
        // acknowledgement first (section 12: "Until it is pressed, Run all stays halted").
        _runAllCarryOnButton.Visible = !atPowerCycleBoundary;
    }

    private void OnRunAllCarryOnClicked()
    {
        // B1: a stray or double click while a half is somehow already active must never start a
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
        RunAllFile.Write(LiveTestRoot(), new RunAllRecord
        {
            Order = RunAllOrder.Items.Select(RunAllFile.Key).ToArray(),
            StoppedAtIndex = _runAllIndex,
            Pointers = new Dictionary<string, string>(_runAllPointers, StringComparer.Ordinal),
        });
    }

    private void StartOrContinueRunAll()
    {
        // B1: Run all's own button stayed enabled during a run; nothing stopped a second click
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

        RunAllRecord? existing = RunAllFile.TryRead(LiveTestRoot());
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

    // section 9.1: "read result.json; show leftAtRest; parse resume.txt; copy result.json and
    // summary.txt to their gui-first-half.* names. ... Then, and only then, the hand-off screen."
    // The snapshot is taken here, additively, before anything else touches this folder again.
    private void ShowHandOff(DisplayRow row, ParsedResult firstHalfResult)
    {
        FirstHalfSnapshot.Take(_activeResultFolder!);

        // M12: the recorded reason lives in the leftAtRest finding's own Detail, not in a
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

    // Priority-zero fix (hosted build crash): the one call site every ChildRunner.MessageReceived
    // and TranscriptLine closure must go through, never a raw BeginInvoke. ReadLoop runs on its
    // own ThreadPool thread and can still be delivering a message the instant this form's handle
    // is destroyed (Dispose, or the STA thread's own teardown in MainFormTestHarness): a plain
    // IsHandleCreated read is not enough, because it can pass and then go stale before BeginInvoke
    // actually runs. This is the one place that race is allowed to happen and be caught: it must
    // never throw out into ReadLoop's own call stack, which nothing there catches, and which is
    // unhandled on a background thread by construction. The failure is recorded, not swallowed:
    // PostDisposalDeliveryFailuresForTests names it for a test, and it is exactly the shape "no
    // silent catches" asks for even though this call is neither COM, CfgMgr32, Bluetooth nor Task
    // Scheduler.
    private void SafeBeginInvoke(Action action)
    {
        // No IsHandleCreated pre-check: that was the original bug. A check on one thread and a
        // BeginInvoke a few CPU cycles later on another can straddle a Dispose happening in
        // between (this form's own OnFormClosing kills the run and lets the close proceed;
        // MainFormTestHarness's cleanup disposes right after its own kill), so IsHandleCreated can
        // read true and then go stale before BeginInvoke actually runs. The try/catch is the only
        // thing here that is actually safe under that race; IsHandleCreated could stay as a
        // cheap first guess, but would add nothing this catch does not already cover.
        try
        {
            BeginInvoke(action);
        }
        catch (ObjectDisposedException ex)
        {
            _postDisposalDeliveryFailures.Add(ex);
        }
        catch (InvalidOperationException ex)
        {
            _postDisposalDeliveryFailures.Add(ex);
        }
    }

    // Test seams only (Earshot.Tests, via InternalsVisibleTo): review round 1's own rule is that
    // a fix whose only proof is an extracted predicate in isolation proves nothing about the
    // caller that used to bypass it. These let a test drive this form's real click handlers
    // headlessly (constructed, handle forced, never Shown) and observe what a real click actually
    // does, the same way MainForm.cs 1090-1150ish's own private methods are wired to controls.
    internal ChildRunner? ActiveRunnerForTests => _activeRunner;

    internal void SafeBeginInvokeForTests(Action action) => SafeBeginInvoke(action);

    internal IReadOnlyList<Exception> PostDisposalDeliveryFailuresForTests => _postDisposalDeliveryFailures;

    // M9 test seams: drives the real silence watchdog tick and lets a test move "the last
    // activity was seen" into the past without a real wait, so both directions (fires on real
    // silence; never fires while a prompt is pending, however long) are provable in milliseconds.
    internal void ForceWatchdogTickForTests() => OnWatchdogTick();

    internal void SetLastActivityUtcForTests(DateTimeOffset utc) => _lastActivityUtc = utc;

    internal int? CurrentPromptSeqForTests => _currentPromptSeq;

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

    internal string RehearsalStatusTextForTests => _rehearsalStatusLabel.Text;

    internal string RehearsalWarningTextForTests => _rehearsalWarningLabel.Text;

    internal void ClickRehearsalButtonForTests() => _rehearsalButton.PerformClick();

    internal bool HandOffVisibleForTests => _handOffBox.Visible;

    internal string HandOffTextForTests => _handOffBox.Text;

    internal bool ResultPanelVisibleForTests => _resultPanel.Visible;

    // M12 test seam: ShowHandOff needs only a DisplayRow and a ParsedResult, both constructible
    // without a real child, so the real production method (not a copy of its logic) is driven
    // directly for the case a live run cannot conveniently reach on its own (a specific
    // leftAtRest reason).
    internal void ShowHandOffForTests(DisplayRow row, ParsedResult firstHalfResult)
    {
        _activeResultFolder ??= Path.Combine(Path.GetTempPath(), "earshot-handoff-test-seam-" + Guid.NewGuid().ToString("N"), row.TestId);
        ShowHandOff(row, firstHalfResult);
    }

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
