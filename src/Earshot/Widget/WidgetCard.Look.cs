namespace Earshot.Widget;

// What the card reads from Windows to look right: the text size and whether transparency effects are on. The
// card asks for it afresh at every Render, so a card that is opened, or redrawn after Windows says something
// changed, always has the current look. The theme and the accent come from the readings the card already takes.
internal sealed partial class WidgetCard
{
    private SystemLook _look = SystemLook.Default;
    private CardType _type = CardType.Plain;
    private Func<SystemLook> _readLook = static () => SystemLook.Default;

    // Where the look comes from (SystemLookService.Current in the tray, a fixed one in a test).
    internal void AttachLook(Func<SystemLook> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        _readLook = read;
    }

    // The look the last Render used, for tests.
    internal SystemLook Look => _look;

    // The text size the last Render laid the card out for, for tests.
    internal double TextScale => _look.TextScale;

    // Reads the look and builds the fonts for it. Called by Render once the display scale is known.
    private void ReadLook()
    {
        _look = _readLook().Clamped();
        _type = new CardType(_dpi, _look.TextScale);
    }

    // True when Windows has changed the look since the last Render, so a shown card needs drawing again.
    internal bool LookHasChanged => _readLook().Clamped() != _look;

    // The card paints its own opaque colour instead of the translucent backdrop when DWM did not give it one,
    // when transparency effects are off and under a high-contrast theme, where Windows shows the colour the person
    // chose in place of acrylic.
    // https://learn.microsoft.com/en-us/windows/apps/design/style/acrylic
    private bool PaintsOpaqueBackground => !_dwmBackdropOk || _look.OpaqueBackground || _palette.HighContrast;
}
