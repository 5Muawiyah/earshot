using Earshot.TestWindow.Core;

namespace Earshot.TestWindow.Ui;

// The window's shell. List mode only, for the single-half rows (slice S4): a row is picked from
// the list, Start begins a real (or, with --sandbox, fake-device) child, StepPanel shows every
// prompt, ResultPanel shows what result.json says once it exits. Two-half rows show their
// derived state but have no Start button yet (their resume handling is slice S6).
internal sealed class MainForm : Form
{
    private readonly string _repoRoot;
    private readonly IReadOnlyList<ManifestRow> _rows;
    private readonly IReadOnlyList<WordingEntry> _wording;
    private readonly SandboxOptions? _sandbox;
    private readonly string _exePath;

    private readonly ListView _rowList;
    private readonly Label _rowDetailLabel;
    private readonly Button _startButton;
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

    private ChildRunner? _activeRunner;
    private string? _activeResultFolder;
    private bool _activeIsResume;
    private TestRowSpec? _activeSpec;
    private BannerState _banner = new() { Level = BannerLevel.None };
    private readonly List<string> _transcript = new();

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
        _wording = wording;
        _sandbox = sandbox;
        _exePath = exePath;

        Text = "Earshot live tests" + (sandbox is not null ? " (SANDBOX, no device)" : string.Empty);
        Width = 1040;
        Height = 720;
        StartPosition = FormStartPosition.CenterScreen;

        // A row shows a plain name and one line saying what the test proves, plus its state, not
        // the TestId alone.
        _rowList = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false,
        };
        _rowList.Columns.Add("#", 34);
        _rowList.Columns.Add("Test", 220);
        _rowList.Columns.Add("What it proves", 300);
        _rowList.Columns.Add("State", 160);
        _rowList.SelectedIndexChanged += (_, _) => { UpdateStartButton(); UpdateRowDetail(); };

        _rowDetailLabel = new Label { Dock = DockStyle.Bottom, Height = 44, AutoEllipsis = false, TextAlign = ContentAlignment.MiddleLeft };

        var leftPanel = new Panel { Dock = DockStyle.Left, Width = 460 };
        leftPanel.Controls.Add(_rowList);
        leftPanel.Controls.Add(_rowDetailLabel);

        _startButton = new Button { Text = "Start", Dock = DockStyle.Top, Height = 32 };
        _startButton.Click += (_, _) => StartSelectedRow();

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

        var rightPanel = new Panel { Dock = DockStyle.Fill };
        rightPanel.Controls.Add(contentHost);
        rightPanel.Controls.Add(_statusLabel);
        rightPanel.Controls.Add(_runAllStatusLabel);
        rightPanel.Controls.Add(_runAllCarryOnButton);
        rightPanel.Controls.Add(_bannerLabel);
        rightPanel.Controls.Add(_caseBox);
        rightPanel.Controls.Add(_startButton);
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
        if (index < 0 || index >= _rows.Count)
        {
            _rowDetailLabel.Text = string.Empty;
            return;
        }

        ManifestRow row = _rows[index];
        _rowDetailLabel.Text = row.Title + Environment.NewLine + "Settles: " + row.Settles;
    }

    // design.md section 4.7: closing while the banner is red asks first.
    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_banner.Level != BannerLevel.Red)
        {
            return;
        }

        DialogResult choice = MessageBox.Show(
            "This PC is not at rest. Close anyway?", "Earshot live tests", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
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

        int selected = _rowList.SelectedIndices.Count > 0 ? _rowList.SelectedIndices[0] : -1;
        _rowList.Items.Clear();
        foreach (ManifestRow row in _rows)
        {
            DerivedRowState state = ComputeState(row);
            var item = new ListViewItem(row.Number);
            item.SubItems.Add(row.Title);
            item.SubItems.Add(row.Settles);
            item.SubItems.Add(RowPresenter.Text(state));
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

    private DerivedRowState ComputeState(ManifestRow row)
    {
        TestRowSpec spec = row.ToSpec();
        IReadOnlyList<RunEvidence> evidence = EvidenceStore.LoadEvidence(LiveTestRoot(), row.TestId);
        DateTimeOffset? exeWrite = File.Exists(_exePath) ? File.GetLastWriteTimeUtc(_exePath) : null;
        return StateDeriver.Derive(spec, evidence, _exePath, exeWrite);
    }

    // A row of Halves 2 (04, 05, 08, 09, 15) is pending when its newest run folder holds
    // resume.txt, its result.json is a first-half result, and it has not been set aside
    // (section 9.2). Test 10's parent row (Variants is not null) has no Start of its own here:
    // its five variants each need their own resume handling, which this slice does not build
    // (see the S6 report's deviations).
    private PendingRun? FindPendingRun(ManifestRow row) =>
        row.Halves == 2 && row.Variants is null ? PendingRunFinder.Find(row.ToSpec(), LiveTestRoot()) : null;

    private void UpdateStartButton()
    {
        int index = _rowList.SelectedIndices.Count > 0 ? _rowList.SelectedIndices[0] : -1;
        if (index < 0 || index >= _rows.Count)
        {
            _startButton.Enabled = false;
            _startButton.Text = "Start";
            return;
        }

        ManifestRow row = _rows[index];
        // The red banner locks every row except 00 Restore (design.md section 4.6).
        bool lockedByBanner = _banner.RowsLockedExceptRestore && row.Number != "00";

        if (row.Variants is not null)
        {
            _startButton.Enabled = false;
            _startButton.Text = "Start";
            return;
        }

        PendingRun? pending = FindPendingRun(row);
        _startButton.Text = pending is not null ? "Carry on with the second half" : "Start";
        _startButton.Enabled = _activeRunner is null && !lockedByBanner;
    }

    private void StartSelectedRow()
    {
        int index = _rowList.SelectedIndices.Count > 0 ? _rowList.SelectedIndices[0] : -1;
        if (index < 0 || index >= _rows.Count)
        {
            return;
        }

        ManifestRow row = _rows[index];
        if (row.Variants is not null)
        {
            return;
        }

        if (_banner.RowsLockedExceptRestore && row.Number != "00")
        {
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

    private void StartFreshRun(string host, ManifestRow row)
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
                host, driver, scriptPath, _exePath, runRoot, resume: false, variant: 0, offerUninstall: false, allowPlanB: false,
                environmentOverrides: _sandbox.ChildEnvironment, extraArguments: extra);
        }
        else
        {
            string driver = Path.Combine(_repoRoot, "tools", "live-tests", "gui", "Invoke-GuiHalf.ps1");
            runner = new ChildRunner(host, driver, scriptPath, _exePath, runRoot, resume: false, variant: 0, offerUninstall: false, allowPlanB: false);
        }

        BeginRun(row, runner, Path.Combine(runRoot, row.TestId), isResume: false);
    }

    // section 9.2: "A resumed run always uses the pending run's -RunRoot; the window never makes
    // a new root for a second half." resume.txt is parsed by ResumeFile, never executed; only its
    // four validated values (script, exe, root, variant) are ever used to start anything.
    private void StartResumedSecondHalf(string host, ManifestRow row, PendingRun pending)
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

    private void BeginRun(ManifestRow row, ChildRunner runner, string resultFolder, bool isResume)
    {
        _activeRunner = runner;
        _activeResultFolder = resultFolder;
        _activeIsResume = isResume;
        _activeSpec = row.ToSpec();
        _transcript.Clear();

        if (_runAllActive)
        {
            // The stamp folder is resultFolder's own parent (<runRoot>\<TestId>): the pointer
            // section 11 asks run-all.json to keep for this item.
            string? stamp = Path.GetFileName(Path.GetDirectoryName(resultFolder));
            if (stamp is not null)
            {
                _runAllPointers[row.Number] = stamp;
            }
        }
        runner.MessageReceived += message =>
        {
            if (IsHandleCreated)
            {
                BeginInvoke(new Action(() => HandleMessage(row, message)));
            }
        };
        runner.TranscriptLine += line =>
        {
            if (IsHandleCreated)
            {
                BeginInvoke(new Action(() => _transcript.Add(line)));
            }
        };

        _resultPanel.Visible = false;
        _handOffBox.Visible = false;
        _stepPanel.Visible = true;
        _statusLabel.Text = "Running " + row.TestId + (isResume ? " (second half)..." : "...");
        UpdateStartButton();
        runner.Start();
    }

    private void HandleMessage(ManifestRow row, ChildMessage message)
    {
        switch (message.Kind)
        {
            case ChildMessageKind.Hello:
                break;
            case ChildMessageKind.Prompt:
                PresentedPrompt presented = PromptPresenter.Present(message, row.Number, _wording, _transcript);
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

    private void OnRunFinished(ManifestRow row)
    {
        _stepPanel.Visible = false;
        _activeRunner?.WaitForExit(TimeSpan.FromSeconds(20));

        string resultPath = Path.Combine(_activeResultFolder!, "result.json");
        (ParsedResult? result, string? failure) = EvidenceStore.TryReadResult(resultPath, row.TestId);

        bool wasFirstHalfOfTwoHalfTest = row.Halves == 2 && !_activeIsResume;
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

    // section 11: the guided sequence. Walks RunAllOrder from _runAllIndex, skipping test 10's
    // five variants (not wired into this window in this build: no variant-level Start exists yet,
    // a broader skip than section 11's own "variant 4 may be skipped for now"). It starts at most
    // one child per call, then returns and waits for OnRunFinished to call back in; it never loops
    // past a row that is not yet a clean pass on disk.
    private void AdvanceRunAll()
    {
        while (_runAllIndex >= 0 && _runAllIndex < RunAllOrder.Items.Count)
        {
            RunAllItem item = RunAllOrder.Items[_runAllIndex];
            if (item.RowNumber == "10")
            {
                _runAllIndex++;
                continue;
            }

            ManifestRow? row = _rows.FirstOrDefault(r => r.Number == item.RowNumber);
            if (row is null)
            {
                _runAllIndex++;
                continue;
            }

            DerivedRowState state = ComputeState(row);
            bool freshEnough = state.Kind is RowStateKind.NotRun or RowStateKind.StoppedBeforeAnyStep;
            if (!freshEnough && RunAllHalt.ShouldHalt(state))
            {
                HaltRunAll(row, state);
                return;
            }

            if (state.Kind == RowStateKind.Passed)
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

    private void HaltRunAll(ManifestRow row, DerivedRowState state)
    {
        SaveRunAllProgress();
        SelectRow(row);

        bool atPowerCycleBoundary = state.Kind is RowStateKind.WaitingForShutDown or RowStateKind.WaitingForRestart;
        _runAllStatusLabel.Text = atPowerCycleBoundary
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

    private void SelectRow(ManifestRow row)
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Number == row.Number)
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
    private void ShowHandOff(ManifestRow row, ParsedResult firstHalfResult)
    {
        FirstHalfSnapshot.Take(_activeResultFolder!);

        var lines = new List<string>
        {
            Copy.LeftAtRestText(firstHalfResult.LeftAtRest, null),
            string.Empty,
        };

        if (firstHalfResult.Overall != "pass")
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
}
