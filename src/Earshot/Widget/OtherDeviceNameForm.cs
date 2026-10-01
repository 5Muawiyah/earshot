namespace Earshot.Widget;

// Lets the owner set their own label for "in use, not on this PC" (WidgetSettings.OtherDeviceLabel is
// where it lands). A modal like DevicePickerForm: one field, OK and Cancel, and the same caption line
// everywhere else this setting is mentioned (WidgetCopy.OtherDeviceCaption), so a reader who has seen the
// setting once recognises the sentence here.
//
// The form has no public properties (WFO1000, the same reason DevicePickerForm has none); the owner
// reads the typed label through Label().
internal sealed class OtherDeviceNameForm : Form
{
    public const string Title = WidgetCopy.OtherDeviceNameTitle;
    public const string OkText = "OK";
    public const string CancelText = "Cancel";

    private readonly TextBox _name;

    // For tests: the caption exactly as shown, so the shared sentence is provable without ever drawing
    // the window (CaptionText), and the field's own cap, so a test can prove it matches the settings
    // constant rather than a number copied by hand.
    internal string CaptionText { get; }

    internal int FieldMaxLength => _name.MaxLength;

    // For tests only: sets the field's text directly, bypassing MaxLength, the same way an OS input method
    // or a pasted string longer than the cap would. A method, not a settable property, for the same reason
    // DevicePickerForm's own test seams (ShowDevices, ShowReadFailed) are methods: WFO1000 flags a settable
    // property on a Form for not configuring design-time serialization, and there is no design-time use of
    // this one to configure.
    internal void SetLabelTextForTest(string text) => _name.Text = text;

    public OtherDeviceNameForm(string currentLabel)
    {
        Text = Title;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        ClientSize = new Size(380, 170);
        MinimumSize = new Size(320, 170);
        Padding = new Padding(12);

        var caption = new Label
        {
            Text = WidgetCopy.OtherDeviceCaption,
            AutoSize = true,
            MaximumSize = new Size(356, 0),
            Margin = new Padding(0, 0, 0, 8),
        };

        _name = new TextBox
        {
            Text = currentLabel ?? "",
            Dock = DockStyle.Fill,
            MaxLength = WidgetSettings.MaxOtherDeviceLabelLength,
        };

        var ok = new FluentButton { Text = OkText, DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new FluentButton { Text = CancelText, DialogResult = DialogResult.Cancel, AutoSize = true };

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0),
            WrapContents = false,
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(caption, 0, 0);
        layout.Controls.Add(_name, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);

        AcceptButton = ok;
        CancelButton = cancel;
        CaptionText = caption.Text;
    }

    // The typed label, cleaned exactly as the settings store cleans it (control, format and separator
    // characters removed, trimmed, cut at a text element): TextBox.MaxLength stops a person typing or
    // pasting past the cap, but does not touch text assigned to Text programmatically, and a paste can
    // carry characters the store would remove, so what leaves the form is already what the store will keep.
    // There is no "unusable" state to refuse here: an empty label is a valid choice (WidgetCopy.OnElsewhere
    // falls back to "On another device" for it), so the caller reads this only once ShowDialog() has
    // returned OK, the same convention DevicePickerForm.Choice() follows.
    internal string Label() => WidgetSettings.CleanedLabel(_name.Text);
}
