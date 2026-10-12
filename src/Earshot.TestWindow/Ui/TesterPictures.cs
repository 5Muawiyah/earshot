using System.Drawing.Imaging;
using System.Globalization;
using Earshot.TestWindow.Core;

namespace Earshot.TestWindow.Ui;

// Draws each view of the window to a PNG from made-up data, so a designer can see the window without running a test, a device or
// anything that touches this PC. The data is invented here: three tests with invented names, no evidence folder, no address, no
// AirPods name. The window has one light look; the dark pictures show the same window with its colours swapped by a palette applied
// here, because the window has no dark mode of its own.
internal static class TesterPictures
{
    internal const string FlagName = "--render";

    private const int WindowWidth = 1150;
    private const int WindowHeight = 800;
    // Set by whoever starts the exe on a desktop that is not the one in use (the private-desktop launcher).
    internal const string PrivateDesktopVariable = "EARSHOT_RENDER_ON_PRIVATE_DESKTOP";

    internal static readonly (string Name, MainForm.PictureView View)[] Views =
    [
        ("home", MainForm.PictureView.Home),
        ("list", MainForm.PictureView.List),
        ("runall", MainForm.PictureView.RunAll),
        ("step", MainForm.PictureView.Step),
        ("waiting", MainForm.PictureView.StepWaiting),
        ("pass", MainForm.PictureView.Pass),
        ("fail", MainForm.PictureView.Fail),
        ("handover", MainForm.PictureView.HandOver),
    ];

    internal static readonly string[] Themes = ["light", "dark"];

    internal static readonly int[] ScalePercents = [100, 150];

    // The file name of one picture.
    internal static string FileName(string view, string theme, int scalePercent) =>
        view + "-" + theme + "-" + scalePercent.ToString(CultureInfo.InvariantCulture) + "pct.png";

    // Whether the caller has said this process runs on a desktop that is not the one in use. A process cannot read which desktop it is on
    // without a native call, which this assembly never makes, so the decision is the caller's: the test supplies its own check, and the
    // exe takes it from PrivateDesktopVariable.
    internal static bool CallerSaysPrivateDesktop() => Environment.GetEnvironmentVariable(PrivateDesktopVariable) == "1";

    // Draws every view, in both themes at both scales, into outDir. Returns the files written.
    internal static IReadOnlyList<string> RenderAll(string outDir, Func<bool> onPrivateDesktop)
    {
        ArgumentException.ThrowIfNullOrEmpty(outDir);
        ArgumentNullException.ThrowIfNull(onPrivateDesktop);
        if (!onPrivateDesktop())
        {
            throw new InvalidOperationException("Pictures are drawn on a private desktop only, never on the desktop in use.");
        }

        Directory.CreateDirectory(outDir);
        string sandbox = Path.Combine(Path.GetTempPath(), "earshot-pictures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        var written = new List<string>();
        try
        {
            PictureData data = PictureData.Build();
            foreach (string theme in Themes)
            {
                foreach (int percent in ScalePercents)
                {
                    using var form = new MainForm(sandbox, data.Rows, Array.Empty<WordingEntry>(), new SandboxOptions { Folder = sandbox }, @"C:\Example\Earshot.exe");
                    form.Size = new Size(WindowWidth, WindowHeight);
                    form.ShowOffScreenForPictures(() => !onPrivateDesktop());
                    var look = new Look(form, percent / 100f, theme == "dark");
                    foreach ((string name, MainForm.PictureView view) in Views)
                    {
                        form.ShowPictureView(view, data.Content);
                        look.Apply();
                        string path = Path.Combine(outDir, FileName(name, theme, percent));
                        Capture(form, path);
                        written.Add(path);
                    }
                }
            }
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }

        return written;
    }

    private static void Capture(Form form, string path)
    {
        Application.DoEvents();
        form.PerformLayout();
        Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppArgb);
        form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
        bitmap.Save(path, ImageFormat.Png);
    }

    // The invented content of the pictures.
    private sealed class PictureData
    {
        public required IReadOnlyList<ManifestRow> Rows { get; init; }

        public required MainForm.PictureContent Content { get; init; }

        public static PictureData Build()
        {
            ManifestRow connect = Row("01", "Connect with one click", "Connects the AirPods from a left click on the icon.", 1, PowerCycleRequirement.None);
            ManifestRow disconnect = Row("02", "Disconnect with one click", "Lets go of the AirPods from a left click on the icon.", 1, PowerCycleRequirement.None);
            ManifestRow everyState = Row("23", "Connect from every state", "Connects the AirPods whatever state they were in.", 2, PowerCycleRequirement.Restart);
            IReadOnlyList<ManifestRow> rows = [connect, disconnect, everyState];
            DisplayRow connectRow = DisplayRow.Flatten(rows)[0];
            DisplayRow disconnectRow = DisplayRow.Flatten(rows)[1];
            DisplayRow everyStateRow = DisplayRow.Flatten(rows)[2];

            // Test 23's own steps, from wording.json: one short line each, each with its picture.
            var caseBlock = new HowToBlock(
                "open-the-case-next-to-this-computer",
                ["Put both AirPods in their case.", "Close the lid and wait ten seconds.", "Open the lid next to this computer, and leave it open."],
                "airpods-case-open");
            var windowsBlock = new HowToBlock(
                "connect-airpods-in-bluetooth-settings",
                ["Click Start.", "Click Settings.", "Click Bluetooth and devices.", "Find your AirPods in the list.", "Click Connect."],
                "bluetooth-connect");
            var iconBlock = new HowToBlock(
                "icon",
                ["Look at the bottom-right corner of the screen, next to the clock.", "Click the Earshot icon once with the left mouse button."],
                "earshot-icon-taskbar");

            var preconditions = new PresentedPrompt
            {
                Heading = "Before this test starts",
                PlainLine = "Are all of those true, and are you ready to start?",
                PlainPhysicalActions =
                [
                    "Three times, connect the AirPods with Earshot, then disconnect and connect them again in Windows settings.",
                    "Set up each state, then connect from the Earshot card, three times each.",
                    "Restart when asked, then connect three more times.",
                ],
                ScriptOwnWords = "Three times, connect the AirPods with Earshot, then disconnect and connect them again through Windows: Settings, Bluetooth and devices.",
                Buttons = [new PromptButton("Yes", "y"), new PromptButton("No", "n")],
                HowToBlocks = [windowsBlock, caseBlock],
            };
            var waiting = new PresentedPrompt
            {
                Heading = "Do this:",
                PlainLine = "Click the Earshot icon, then Connect. Wait for the card.",
                DetailLabel = "Have you done it?",
                ScriptOwnWords = "Left-click the Earshot icon, then Connect on the card. Wait until the card says the AirPods are connected or gives a reason, then come back here.",
                Buttons = [new PromptButton("Yes", string.Empty), new PromptButton("No", string.Empty, SendsReply: false)],
                HowToBlocks = [iconBlock],
            };

            var passed = new ResultPresentation
            {
                Failures = [],
                Errors = [],
                TotalCriteria = 6,
                FailedCount = 0,
                InconclusiveCount = 0,
                PlainFailureLines = [],
                LeftAtRest = "yes",
                EvidenceFolder = @"C:\Example\Earshot\livetest\01\run-0001",
                ResultJsonPath = @"C:\Example\Earshot\livetest\01\run-0001\result.json",
                SummaryTxtPath = @"C:\Example\Earshot\livetest\01\run-0001\summary.txt",
            };
            var failed = new ResultPresentation
            {
                Failures = [new FailureRow("example-connect", "connected", "not connected")],
                Errors = [],
                TotalCriteria = 6,
                FailedCount = 1,
                InconclusiveCount = 1,
                PlainFailureLines =
                [
                    new PlainCheckLine("example-connect", "The AirPods did not connect.", "This computer could not reach them.", true, "fail"),
                    new PlainCheckLine("example-sound", "It could not be told whether sound was playing.", null, true, "inconclusive"),
                ],
                LeftAtRest = "yes",
                EvidenceFolder = @"C:\Example\Earshot\livetest\02\run-0001",
                ResultJsonPath = @"C:\Example\Earshot\livetest\02\run-0001\result.json",
                SummaryTxtPath = @"C:\Example\Earshot\livetest\02\run-0001\summary.txt",
            };

            return new PictureData
            {
                Rows = rows,
                Content = new MainForm.PictureContent(
                    everyStateRow,
                    Copy.StepProgressLine(3, 3, everyStateRow.Name),
                    preconditions,
                    waiting,
                    passed,
                    failed,
                    [Copy.LeftAtRestText("yes", null), string.Empty, Copy.HandOffText(PowerCycleRequirement.Restart)]),
            };
        }

        private static ManifestRow Row(string number, string name, string proves, int halves, PowerCycleRequirement requirement) => new()
        {
            Number = number,
            Script = number + "-Example.ps1",
            TestId = number + "-example",
            Kind = "example",
            Halves = halves,
            Name = name,
            Title = name,
            Proves = proves,
            Settles = proves,
            PowerCycleRequirement = requirement,
        };
    }

    // The scale and colours of one window, applied again after every view because a view makes new controls.
    private sealed class Look
    {
        private static readonly Color DarkBack = Color.FromArgb(0x20, 0x20, 0x20);
        private static readonly Color DarkField = Color.FromArgb(0x2B, 0x2B, 0x2B);
        private static readonly Color DarkButton = Color.FromArgb(0x38, 0x38, 0x38);
        private static readonly Color DarkEdge = Color.FromArgb(0x6B, 0x6B, 0x6B);
        private static readonly Color DarkText = Color.FromArgb(0xF2, 0xF2, 0xF2);
        private static readonly Color DarkGray = Color.FromArgb(0xA6, 0xA6, 0xA6);

        private readonly MainForm _form;
        private readonly float _layout;
        private readonly float _font;
        private readonly bool _dark;
        private readonly HashSet<Control> _scaled = new(ReferenceEqualityComparer.Instance);

        public Look(MainForm form, float scale, bool dark)
        {
            _form = form;
            _layout = scale;

            // A font's size is in points, and a point is DeviceDpi / 72 pixels, so a window at scale s needs points of s * 96 / DeviceDpi.
            _font = scale * 96f / Math.Max(1, form.DeviceDpi);
            _dark = dark;
            if (Math.Abs(scale - 1f) > 0.001f)
            {
                form.Scale(new SizeF(scale, scale));
            }

            form.Font = Scaled(form.Font);
            _scaled.Add(form);
        }

        public void Apply()
        {
            foreach (Control child in _form.Controls)
            {
                Walk(child);
            }

            if (_dark)
            {
                _form.BackColor = DarkBack;
                _form.ForeColor = DarkText;
            }
        }

        private Font Scaled(Font font) => new(font.FontFamily, font.Size * _font, font.Style, font.Unit);

        private void Walk(Control control)
        {
            if (_scaled.Add(control))
            {
                if (control.Parent is { } parent && !ReferenceEquals(control.Font, parent.Font))
                {
                    control.Font = Scaled(control.Font);
                }

                if (Math.Abs(_layout - 1f) > 0.001f && control.MinimumSize != Size.Empty && IsLateControl(control))
                {
                    control.MinimumSize = new Size((int)(control.MinimumSize.Width * _layout), (int)(control.MinimumSize.Height * _layout));
                }
            }

            if (_dark)
            {
                Paint(control);
            }

            foreach (Control child in control.Controls)
            {
                Walk(child);
            }
        }

        // A control made after the window was scaled (the answer buttons of a step), whose minimum size is still at 100%.
        private static bool IsLateControl(Control control) => control is Button;

        private static void Paint(Control control)
        {
            Color parentText = control.Parent?.ForeColor ?? DarkText;
            switch (control)
            {
                case Button button:
                    button.FlatStyle = FlatStyle.Flat;
                    button.FlatAppearance.BorderColor = DarkEdge;
                    button.BackColor = DarkButton;
                    button.ForeColor = DarkText;
                    break;
                case ListView list:
                    list.BackColor = DarkField;
                    list.ForeColor = DarkText;
                    foreach (ListViewItem item in list.Items)
                    {
                        item.ForeColor = Map(item.ForeColor);
                    }

                    break;
                case TextBox or ComboBox:
                    control.BackColor = DarkField;
                    control.ForeColor = DarkText;
                    break;
                default:
                    if (control.ForeColor != parentText)
                    {
                        control.ForeColor = Map(control.ForeColor);
                    }

                    break;
            }
        }

        // The window's light-theme colours, each swapped for one that reads on the dark back; any other colour stays.
        private static Color Map(Color color)
        {
            int argb = color.ToArgb();
            if (argb == SystemColors.GrayText.ToArgb())
            {
                return DarkGray;
            }

            if (argb == Color.DarkRed.ToArgb())
            {
                return Color.FromArgb(0xFF, 0x8A, 0x80);
            }

            if (argb == Color.DarkGreen.ToArgb() || argb == Color.Green.ToArgb())
            {
                return Color.FromArgb(0x7F, 0xD9, 0x9A);
            }

            if (argb == Color.DarkGoldenrod.ToArgb())
            {
                return Color.FromArgb(0xE6, 0xC5, 0x5C);
            }

            if (argb == Color.DarkSlateBlue.ToArgb())
            {
                return Color.FromArgb(0xA9, 0xB4, 0xFF);
            }

            return argb == Color.Black.ToArgb() ? DarkText : color;
        }
    }
}
