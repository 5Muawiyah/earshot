using System.Drawing;
using System.Windows.Forms;

namespace Earshot.Repair;

// The pictures a prompt may show, embedded in Earshot from the same files the test window's how-to blocks use
// (src/Earshot.TestWindow/Data/pictures, drawn schematics, see its README.txt). A name with no picture loads nothing, and the
// prompt then shows its caption alone.
internal static class PromptPictures
{
    // The two ways into pairing mode, each with its line: Pair AirPods' first question and Fix's pairing question.
    public static IReadOnlyList<PromptPicture> PairingMode =>
        [new PromptPicture(CaseFront, Earshot.Pairing.PairingText.CaseFront), new PromptPicture(CaseBack, Earshot.Pairing.PairingText.CaseBack)];

    // The paragraphs under a question's own words that the pictures' window shows: the iPhone line, then each case line (which
    // that window puts under its picture instead).
    public static string PairingModeSteps =>
        Earshot.Pairing.PairingText.TurnOffIPhoneBluetooth + Environment.NewLine + Environment.NewLine +
        Earshot.Pairing.PairingText.CaseFront + Environment.NewLine + Environment.NewLine +
        Earshot.Pairing.PairingText.CaseBack;

    public const string CaseFront = "airpods-case-front-tap";
    public const string CaseBack = "airpods-case-back-button";

    internal const string ResourcePrefix = "Earshot.Pictures.";

    // The picture, or null when Earshot carries none of that name. The caller disposes it.
    public static Image? Load(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        using Stream? stream = typeof(PromptPictures).Assembly.GetManifestResourceStream(ResourcePrefix + name + ".png");
        if (stream is null)
        {
            return null;
        }

        // Image.FromStream needs its stream for the life of the image, so the picture is copied into a bitmap of its own.
        using Image read = Image.FromStream(stream);
        return new Bitmap(read);
    }
}

// A question with pictures: the heading, the body's lines that are not a caption, each picture with its caption under it, and
// the OK and Cancel buttons, Cancel the default as in the TaskDialog questions. It times out as they do (a cancel, said as
// TimedOut), and closeOn closes it as a cancel. No tooltips: every control has its words on it.
internal static class PicturePromptWindow
{
    public static FixPromptAnswer Ask(FixPrompt prompt, CancellationToken closeOn) => Ask(prompt, FixDialogPages.Timeout, closeOn);

    internal static FixPromptAnswer Ask(FixPrompt prompt, TimeSpan limit, CancellationToken closeOn)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        using Form form = Build(prompt, out Button ok);
        bool timedOut = false;
        using var timer = new System.Windows.Forms.Timer { Interval = (int)limit.TotalMilliseconds };
        timer.Tick += (_, _) =>
        {
            timedOut = true;
            timer.Stop();
            form.Close();
        };
        form.Shown += (_, _) => timer.Start();
        SynchronizationContext? ui = SynchronizationContext.Current;
        using CancellationTokenRegistration closing = closeOn.Register(() => ui?.Post(_ => form.Close(), null));

        DialogResult result = form.ShowDialog();
        FixAnswer answer = timedOut ? FixAnswer.TimedOut : result == DialogResult.OK && !closeOn.IsCancellationRequested ? FixAnswer.Ok : FixAnswer.Cancel;
        return new FixPromptAnswer(answer, -1);
    }

    // The window, not shown. ok is the button that answers OK.
    internal static Form Build(FixPrompt prompt, out Button ok)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        IReadOnlyList<PromptPicture> pictures = prompt.Pictures ?? [];
        var captions = new HashSet<string>(pictures.Select(p => p.Caption), StringComparer.Ordinal);
        string intro = string.Join(
            Environment.NewLine + Environment.NewLine,
            prompt.Body.Split([Environment.NewLine + Environment.NewLine, "\n\n"], StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => !captions.Contains(p)));

        var form = new Form
        {
            Text = prompt.Title,
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = true,
            AutoScaleMode = AutoScaleMode.Dpi,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16),
        };

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label { Text = prompt.Heading, AutoSize = true, MaximumSize = new Size(640, 0), Font = new Font(form.Font.FontFamily, form.Font.Size * 1.25f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 8) });
        if (intro.Length > 0)
        {
            layout.Controls.Add(new Label { Text = intro, AutoSize = true, MaximumSize = new Size(640, 0), Margin = new Padding(0, 0, 0, 12) });
        }

        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        foreach (PromptPicture picture in pictures)
        {
            var column = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 16, 0) };
            Image? image = PromptPictures.Load(picture.Name);
            if (image is not null)
            {
                var box = new PictureBox { Image = image, SizeMode = PictureBoxSizeMode.AutoSize, AccessibleName = picture.Caption, AccessibleRole = AccessibleRole.Graphic };
                form.Disposed += (_, _) => image.Dispose();
                column.Controls.Add(box);
            }

            column.Controls.Add(new Label { Text = picture.Caption, AutoSize = true, MaximumSize = new Size(300, 0) });
            row.Controls.Add(column);
        }

        layout.Controls.Add(row);

        ok = new Button { Text = prompt.OkText, AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Right, Margin = new Padding(0, 16, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        layout.Controls.Add(buttons);
        form.Controls.Add(layout);
        form.CancelButton = cancel;
        // Enter presses Cancel, as the TaskDialog questions' default button: nothing starts unless the person picks it.
        form.AcceptButton = cancel;
        form.ActiveControl = cancel;
        return form;
    }
}
