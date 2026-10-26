using System.Windows.Forms;
using Earshot.Pairing;

namespace Earshot.Repair;

// The part of the re-pair the person sees while it runs: a small window of its own with the instruction and a Cancel button,
// and dialogs for the pick and the pairing numbers. It runs on the UI thread of the fix mode.
internal sealed class FixRePairWindow : IFixRePairUi, IDisposable
{
    private readonly CancellationTokenSource _cancel;
    private readonly string _title;
    private Form? _form;
    private Label? _label;

    // cancel: cancelled when the person presses Cancel in the window. title: what the window and its dialogs are called, the Fix
    // connection title unless Pair AirPods is the one running.
    public FixRePairWindow(CancellationTokenSource cancel, string title = FixPromptText.Title)
    {
        ArgumentNullException.ThrowIfNull(cancel);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        _cancel = cancel;
        _title = title;
    }

    public void ShowInstruction(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_form is null)
        {
            _form = new Form
            {
                Text = _title,
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                ClientSize = new System.Drawing.Size(460, 190),
                ShowInTaskbar = true,
            };
            _label = new Label { Left = 16, Top = 16, Width = 428, Height = 110, Text = text };
            var cancel = new Button { Text = "Cancel", Left = 364, Top = 144, Width = 80 };
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
        FixPromptAnswer answer = new TaskDialogPrompter().Ask(PickPrompt(candidates, _title));
        return Task.FromResult<UnpairedDevice?>(answer.Answer == FixAnswer.Ok && answer.Chosen >= 0 ? candidates[answer.Chosen] : null);
    }

    // One row per candidate, none chosen for the person; pressing the button is the "Pair" press on that one device.
    internal static FixPrompt PickPrompt(IReadOnlyList<UnpairedDevice> candidates, string title = FixPromptText.Title)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return new FixPrompt(
            title,
            PairingText.Choose,
            "These Bluetooth headphones are in pairing mode nearby. Choose your AirPods, then press Pair. If none is yours, cancel.",
            [],
            candidates.Select(c => string.IsNullOrWhiteSpace(c.Name) ? PairingText.NoName : c.Name).ToList(),
            PairingText.PairButton);
    }

    public Task<bool> ConfirmPinAsync(FixPairKind kind, string? pin) =>
        Task.FromResult(new TaskDialogPrompter().Ask(PinPrompt(kind, pin, _title)).Answer == FixAnswer.Ok);

    internal static FixPrompt PinPrompt(FixPairKind kind, string? pin, string title = FixPromptText.Title) => kind == FixPairKind.ConfirmPinMatch
        ? new FixPrompt(title, PairingText.DoesItShow(pin), "Compare the number with the one on your device.", [], [], "Yes")
        : new FixPrompt(title, "Enter " + pin + " on your device", "Windows is showing this number to pair.", [], [], "Continue");

    public void ShowResult(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Close();
        var page = new TaskDialogPage
        {
            Caption = _title,
            Heading = _title,
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
