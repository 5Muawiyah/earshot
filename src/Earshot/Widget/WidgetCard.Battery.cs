using System.Drawing.Drawing2D;
using IconId = Earshot.Icons.Catalogue.Icon;

namespace Earshot.Widget;

// What the card says about the battery beyond the three columns: the read line, and the ink a part that is not
// fresh is drawn in. The values themselves come from the model's ShownParts (BatteryFreshness), so a part's age
// is decided in one place and the card only picks a colour.
internal sealed partial class WidgetCard
{
    // The line under the where line: Windows' own figure when that is what is shown ("Windows reads 70%"), else
    // when the battery was last read ("Battery read 4 min ago"). The age is the newest part's: a part that is old
    // greys by itself, and must not make buds that were just heard read as old. Empty while that reading is fresh: no age is
    // shown for a live value (WidgetCopy.ReadAge), and the line's place is kept.
    internal string ReadLineText =>
        _model.Refresh?.ReadLine is { } refreshing ? refreshing
        : _model.ShownParts.WindowsPercent is int figure
            ? WidgetCopy.WindowsReads(figure)
            : AsksToOpenTheCase
                ? WidgetCopy.OpenTheCaseToShowBattery
                : WidgetCopy.BatteryReadLine(_model.ShownParts.NewestReadAt, _model.Now);

    // The AirPods are connected to this PC, no pair is linked and no figure of any kind is shown: the line says what makes
    // one show, with an earbud icon, in the place the age takes once a battery has been read.
    internal bool AsksToOpenTheCase =>
        !_unasked
        && _model.Snapshot.Where == AirPodsWhere.ThisPc
        && _model.Snapshot.Selection == BroadcastSelectionState.Listening
        && _model.ShownParts.NewestReadAt is null;

    // The same line as drawn beside its clock icon: the age alone ("4 min ago"), since the icon says what is aged. The
    // full line is the tooltip. A line that is not an age (a refresh under way, Windows' own figure) is the same short or
    // long.
    internal string ReadLineShort =>
        _model.Refresh?.ReadLine is { } refreshing ? refreshing
        : _model.ShownParts.WindowsPercent is int figure
            ? WidgetCopy.WindowsReads(figure)
            : AsksToOpenTheCase
                ? WidgetCopy.OpenTheCaseToShowBattery
                : WidgetCopy.ReadAge(_model.ShownParts.NewestReadAt, _model.Now);

    // The refresh icon's tooltip: what is happening while it turns, otherwise its name.
    internal string RefreshTip => _model.Refresh is { Reading: true } ? WidgetCopy.ReadingBattery : WidgetCopy.RefreshBattery;

    // Whether the refresh icon is turning, so the presenter only runs its animation while one is.
    internal bool RefreshReading => _model.Refresh is { Reading: true };

    // Moves the turning icon on and repaints only it.
    internal void SetRefreshFrame(int frame)
    {
        if (_model.Refresh is not { Reading: true } view)
        {
            return;
        }

        _model = _model with { Refresh = view with { SpinFrame = frame } };
        if (IsHandleCreated && !_mainLayout.Refresh.IsEmpty)
        {
            Invalidate(_mainLayout.Refresh);
        }
    }

    private void DrawRefreshIcon(Graphics g, WidgetCardLayout.Layout layout)
    {
        if (layout.Refresh.IsEmpty)
        {
            return;
        }

        CardColours colours = Colours;
        bool reading = _model.Refresh is { Reading: true };
        Color ink = reading ? colours.Accent : RefreshDisabled ? colours.TextDisabled : colours.Text;
        Rectangle bounds = layout.Refresh;

        // The turn is baked into the icon's mask, a tenth of a turn a frame, so the icon is still drawn pixel for pixel.
        int turn = reading ? _model.Refresh!.SpinFrame % BatteryRefreshView.SpinFrames : 0;
        CardPaint.DrawIcon(g, IconId.Refresh, filled: false, bounds, ink, _dpi, CardPaint.IconBoxAt96, _look.TextScale, rotationStep: turn);
        if (_focus == WidgetCardFocus.Refresh && FocusShown)
        {
            CardPaint.Focus(g, bounds, CardPlacement.Scale(FocusVisual.ControlRadiusAt96, _dpi), colours, _dpi);
        }
    }

}
