using System.Windows.Forms;
using Earshot.Pairing;

namespace Earshot.Repair;

// The part of the re-pair the person sees while it runs: a small window of its own with the instruction and a Cancel button,
// and dialogs for the pick and the pairing numbers. The pairing session resumes on pool threads after its awaits, so every call
// is sent to the thread the window was made on (the fix mode's UI thread, or the tray's), where its form and dialogs live.
// https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.windowsformssynchronizationcontext
internal sealed class FixRePairWindow : IFixRePairUi, IDisposable
{
    private readonly CancellationTokenSource _cancel;
    private readonly string _title;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private readonly int _uiThread = Environment.CurrentManagedThreadId;
    private Form? _form;
    private Label? _label;

    // Set while the window is closed by the run itself, so only the person's close (the X, or Alt+F4) cancels the run.
    private bool _closingHere;

    internal Form? FormForTests => _form;

    // Runs work on the window's own thread, waiting for it; at once when already there or when there is no context to send to.
    internal T OnUi<T>(Func<T> work)
    {
        if (_ui is null || Environment.CurrentManagedThreadId == _uiThread)
        {
            return work();
        }

        T result = default!;
        _ui.Send(_ => result = work(), null);
        return result;
    }

    private void OnUi(Action work) => OnUi(() =>
    {
        work();
        return true;
    });

    // The run's questions, asked on this window's thread: Fix resumes on a pool thread after the administrator run, and its
    // second question is a window with pictures, which belongs on the thread whose message loop runs it.
    internal IFixPrompter AskingOnThisThread(IFixPrompter inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return new OnWindowThreadPrompter(this, inner);
    }

    private sealed class OnWindowThreadPrompter(FixRePairWindow window, IFixPrompter inner) : IFixPrompter
    {
        public FixPromptAnswer Ask(FixPrompt prompt) => window.OnUi(() => inner.Ask(prompt));

        public FixPromptAnswer Ask(FixPrompt prompt, CancellationToken closeOn) => window.OnUi(() => inner.Ask(prompt, closeOn));
    }

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
        OnUi(() => ShowInstructionHere(text));
    }

    private void ShowInstructionHere(string text)
    {
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

            // Closing the window is the person's Cancel too: before, it closed the window and the scan ran on for its minute
            // behind nothing, with the lock and the pairing flag held.
            // https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.formclosingeventargs.closereason
            _form.FormClosing += (_, e) =>
            {
                if (e.CloseReason == CloseReason.UserClosing && !_closingHere)
                {
                    _cancel.Cancel();
                }
            };
            _form.FormClosed += (_, _) =>
            {
                if (!_closingHere)
                {
                    _form = null;
                    _label = null;
                }
            };
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
        FixPromptAnswer answer = OnUi(() => new TaskDialogPrompter().Ask(PickPrompt(candidates, _title), _cancel.Token));
        return Task.FromResult<UnpairedDevice?>(answer.Answer == FixAnswer.Ok && answer.Chosen >= 0 ? candidates[answer.Chosen] : null);
    }

    // One row per candidate, none chosen for the person; pressing the button is the "Pair" press on that one device.
    internal static FixPrompt PickPrompt(IReadOnlyList<UnpairedDevice> candidates, string title = FixPromptText.Title)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return new FixPrompt(
            title,
            PairingText.Choose,
            "These Bluetooth headphones are nearby and can be paired. Choose your AirPods, then press Pair. If none is yours, cancel.",
            [],
            candidates.Select(c => string.IsNullOrWhiteSpace(c.Name) ? PairingText.NoName : c.Name).ToList(),
            PairingText.PairButton);
    }

    public Task<bool> ConfirmPinAsync(FixPairKind kind, string? pin, CancellationToken ct) =>
        Task.FromResult(OnUi(() => new TaskDialogPrompter().Ask(PinPrompt(kind, pin, _title), ct).Answer == FixAnswer.Ok));

    internal static FixPrompt PinPrompt(FixPairKind kind, string? pin, string title = FixPromptText.Title) => kind == FixPairKind.ConfirmPinMatch
        ? new FixPrompt(title, PairingText.DoesItShow(pin), "Compare the number with the one on your device.", [], [], "Yes")
        : new FixPrompt(title, "Enter " + pin + " on your device", "Enter this number on your device to pair.", [], [], "Continue");

    // With offerTrace, a second button asks the tray for Record a Bluetooth trace, which shows its note before anything is
    // recorded.
    public bool ShowResult(string text, bool offerTrace)
    {
        ArgumentNullException.ThrowIfNull(text);
        return OnUi(() => ShowResultHere(text, offerTrace));
    }

    private bool ShowResultHere(string text, bool offerTrace)
    {
        CloseHere();
        var page = new TaskDialogPage
        {
            Caption = _title,
            Heading = _title,
            Text = text,
            AllowCancel = true,
        };
        var trace = new TaskDialogButton(FixConnection.RecordTraceButton);
        if (offerTrace)
        {
            page.Buttons.Add(trace);
        }

        page.Buttons.Add(TaskDialogButton.OK);
        return TaskDialog.ShowDialog(page) == trace;
    }

    public void Close() => OnUi(CloseHere);

    private void CloseHere()
    {
        _closingHere = true;
        try
        {
            _form?.Close();
            _form?.Dispose();
        }
        finally
        {
            _closingHere = false;
        }

        _form = null;
        _label = null;
    }

    public void Dispose() => Close();
}
