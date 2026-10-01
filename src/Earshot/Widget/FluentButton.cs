using Earshot.Popup;

namespace Earshot.Widget;

// A button for the dialogs that never draws the dotted focus rectangle. Windows draws that rectangle when the
// keyboard is used; ShowFocusCues is the hook the framework asks first, and false here removes it. In its place
// the button paints the Windows 11 focus visual when it has the focus and the keyboard has been used (the
// framework's own answer, read through the base class).
//
// A child control cannot paint outside its own bounds, so the visual sits just inside them instead of just
// outside, the one difference from the card's. The dialog keeps a few pixels between its buttons so it stays
// readable.
internal sealed class FluentButton : Button
{
    public FluentButton()
    {
        // Standard, not System: the framework paints a Standard button itself and asks ShowFocusCues, where a
        // System button is painted by Windows and draws its own rectangle regardless.
        FlatStyle = FlatStyle.Standard;
    }

    protected override bool ShowFocusCues => false;

    // True while the focus visual would show: the button is focused and the keyboard has been used.
    internal bool FocusVisualShowing => Focused && base.ShowFocusCues;

    protected override void OnPaint(PaintEventArgs pevent)
    {
        base.OnPaint(pevent);
        ArgumentNullException.ThrowIfNull(pevent);
        if (FocusVisualShowing)
        {
            DrawFocusVisual(pevent.Graphics);
        }
    }

    // Paints the visual inset so its outer edge meets the button's own edge. For the test too.
    internal void DrawFocusVisual(Graphics g)
    {
        int dpi = DeviceDpi;
        int reach = FocusMetrics.For(dpi).Reach;
        Rectangle inside = Rectangle.Inflate(ClientRectangle, -reach, -reach);
        bool dark = SystemColors.Window.GetBrightness() < 0.5f;
        FocusVisual.Draw(g, inside, CardPlacement.Scale(FocusVisual.ControlRadiusAt96, dpi), dpi, FocusPalette.For(dark, SystemInformation.HighContrast));
    }
}
