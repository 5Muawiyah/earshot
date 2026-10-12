using System.Windows.Forms;

namespace Earshot.Repair;

// The part of the re-pair the person sees while it runs: a small window of its own with the instruction and a Cancel button,
// and dialogs for the pick and the pairing numbers. It runs on the UI thread of the fix mode.
internal sealed class FixRePairWindow : IFixRePairUi, IDisposable
{
    private readonly CancellationTokenSource _cancel;
    private Form? _form;
    private Label? _label;

    // cancel: cancelled when the person presses Cancel in the window.
    public FixRePairWindow(CancellationTokenSource cancel)
    {
        ArgumentNullException.ThrowIfNull(cancel);
        _cancel = cancel;
    }

    public void ShowInstruction(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_form is null)
        {
            _form = new Form
            {
                Text = FixPromptText.Title,
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                ClientSize = new System.Drawing.Size(460, 170),
                ShowInTaskbar = true,
            };
            _label = new Label { Left = 16, Top = 16, Width = 428, Height = 90, Text = text };
            var cancel = new Button { Text = "Cancel", Left = 364, Top = 124, Width = 80 };
            cancel.Click += (_, _) => _cancel.Cancel();
            _form.Controls.Add(_label);
            _form.Controls.Add(cancel);
            _form.CancelButton = cancel;
            _form.Show();
        }
        else if (_label is not null)
        {
            _label.Text = text;
        }
    }

    public Task<UnpairedDevice?> PickAsync(IReadOnlyList<UnpairedDevice> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var prompt = new FixPrompt(
            FixPromptText.Title,
            "Which device is your AirPods?",
            "Earshot found these Bluetooth audio devices that appeared just now. Pick your AirPods. If none is yours, cancel.",
            [],
            candidates.Select(c => string.IsNullOrWhiteSpace(c.Name) ? "Unnamed device" : c.Name).ToList(),
            "Pair this one");
        FixPromptAnswer answer = new TaskDialogPrompter().Ask(prompt);
        return Task.FromResult<UnpairedDevice?>(answer.Answer == FixAnswer.Ok && answer.Chosen >= 0 ? candidates[answer.Chosen] : null);
    }

    public Task<bool> ConfirmPinAsync(FixPairKind kind, string? pin)
    {
        string text = kind == FixPairKind.ConfirmPinMatch
            ? "Does this number match the one shown on the AirPods or the device? " + pin
            : "Windows is showing this number to pair: " + pin;
        var prompt = new FixPrompt(FixPromptText.Title, "Pairing number", text, [], [], kind == FixPairKind.ConfirmPinMatch ? "It matches" : "Continue");
        return Task.FromResult(new TaskDialogPrompter().Ask(prompt).Answer == FixAnswer.Ok);
    }

    public void ShowResult(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Close();
        var page = new TaskDialogPage
        {
            Caption = FixPromptText.Title,
            Heading = "Fix connection",
            Text = text,
            AllowCancel = true,
        };
        page.Buttons.Add(TaskDialogButton.OK);
        TaskDialog.ShowDialog(page);
    }

    public void Close()
    {
        _form?.Close();
        _form?.Dispose();
        _form = null;
        _label = null;
    }

    public void Dispose() => Close();
}
