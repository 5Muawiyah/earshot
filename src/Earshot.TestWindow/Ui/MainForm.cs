using Earshot.TestWindow.Core;

namespace Earshot.TestWindow.Ui;

// The window's shell. List mode only, for the single-half rows (slice S4): a row is picked from
// the list, Start begins a real (or, with --sandbox, fake-device) child, and StepPanel shows
// every prompt. Two-half rows show their derived state but have no Start button yet (their
// resume handling is slice S6). What happens once a half exits (ResultPanel, the banner) is
// slice S5.
internal sealed class MainForm : Form
{
    private readonly string _repoRoot;
    private readonly IReadOnlyList<ManifestRow> _rows;
    private readonly IReadOnlyList<WordingEntry> _wording;
    private readonly SandboxOptions? _sandbox;
    private readonly string _exePath;

    private readonly ListBox _rowList;
    private readonly Button _startButton;
    private readonly ComboBox _caseBox;
    private readonly Label _statusLabel;
    private readonly StepPanel _stepPanel;

    private ChildRunner? _activeRunner;
    private string? _activeResultFolder;

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

        _rowList = new ListBox { Dock = DockStyle.Left, Width = 300, IntegralHeight = false };
        _rowList.SelectedIndexChanged += (_, _) => UpdateStartButton();

        _startButton = new Button { Text = "Start", Dock = DockStyle.Top, Height = 32 };
        _startButton.Click += (_, _) => StartSelectedRow();

        _caseBox = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, Visible = sandbox is not null };
        _caseBox.Items.AddRange(new object[] { "none", "one", "two" });
        _caseBox.SelectedIndex = 1;

        _statusLabel = new Label { Dock = DockStyle.Top, Height = 28, TextAlign = ContentAlignment.MiddleLeft };

        var contentHost = new Panel { Dock = DockStyle.Fill };
        _stepPanel = new StepPanel { Visible = false };
        contentHost.Controls.Add(_stepPanel);

        var rightPanel = new Panel { Dock = DockStyle.Fill };
        rightPanel.Controls.Add(contentHost);
        rightPanel.Controls.Add(_statusLabel);
        rightPanel.Controls.Add(_caseBox);
        rightPanel.Controls.Add(_startButton);

        Controls.Add(rightPanel);
        Controls.Add(_rowList);

        PopulateRows();
        UpdateStartButton();
    }

    private string LiveTestRoot() => _sandbox is not null
        ? Path.Combine(_sandbox.Folder, "local", "Earshot", "livetest")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Earshot", "livetest");

    private void PopulateRows()
    {
        int selected = _rowList.SelectedIndex;
        _rowList.Items.Clear();
        foreach (ManifestRow row in _rows)
        {
            DerivedRowState state = ComputeState(row);
            _rowList.Items.Add(row.Number + "  " + row.TestId + "   [" + RowPresenter.Text(state) + "]");
        }

        if (selected >= 0 && selected < _rowList.Items.Count)
        {
            _rowList.SelectedIndex = selected;
        }
    }

    private DerivedRowState ComputeState(ManifestRow row)
    {
        TestRowSpec spec = row.ToSpec();
        IReadOnlyList<RunEvidence> evidence = EvidenceStore.LoadEvidence(LiveTestRoot(), row.TestId);
        DateTimeOffset? exeWrite = File.Exists(_exePath) ? File.GetLastWriteTimeUtc(_exePath) : null;
        return StateDeriver.Derive(spec, evidence, _exePath, exeWrite);
    }

    private void UpdateStartButton()
    {
        int index = _rowList.SelectedIndex;
        _startButton.Enabled = index >= 0 && index < _rows.Count && _rows[index].Halves == 1 && _activeRunner is null;
    }

    private void StartSelectedRow()
    {
        int index = _rowList.SelectedIndex;
        if (index < 0 || index >= _rows.Count)
        {
            return;
        }

        ManifestRow row = _rows[index];
        if (row.Halves != 1)
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

        _activeRunner = runner;
        _activeResultFolder = Path.Combine(runRoot, row.TestId);
        runner.MessageReceived += message =>
        {
            if (IsHandleCreated)
            {
                BeginInvoke(new Action(() => HandleMessage(row, message)));
            }
        };

        _stepPanel.Visible = true;
        _statusLabel.Text = "Running " + row.TestId + "...";
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
                PresentedPrompt presented = PromptPresenter.Present(message, row.Number, _wording);
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
        _statusLabel.Text = result is not null
            ? row.TestId + " finished: " + result.Overall
            : "No readable result.json: " + failure;

        _activeRunner = null;
        PopulateRows();
        UpdateStartButton();
    }
}
