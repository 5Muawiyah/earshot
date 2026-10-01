using Earshot.Widget;

namespace Earshot.App;

// The cards follow the system's look live: the theme, the accent, the text size, transparency effects and a
// high-contrast theme. Windows says so in two ways, a settings change message to the hidden window and, for the
// text size and transparency, an event from UISettings that is not documented to fire in a process with no core
// window. Either one asks an open card to read its look again; every show reads it afresh too.
internal sealed partial class TrayContext
{
    private SystemLookService? _look;
    private EventHandler? _lookChanged;
    private EventHandler? _lookSettingChanged;

    // Hooks an open card up to the look. Called once, when the card's presenter is built.
    private void WireLook()
    {
        if (_look is not null)
        {
            return;
        }

        SystemLookService look = LookService();
        _look = look;
        _lookChanged = (_, _) => ReapplyCardLook();
        _lookSettingChanged = (_, _) =>
        {
            // Poke raises Changed, and so a re-draw, when the look itself changed. A settings change that is not the look (the
            // theme or the accent) still needs the cards drawn again; one draw for either, not two.
            int before = _lookReappliesAsked;
            look.Poke();
            if (_lookReappliesAsked == before)
            {
                ReapplyCardLook();
            }
        };
        look.Changed += _lookChanged;
        _window.SettingChanged += _lookSettingChanged;
    }

    // Lets go of the look when the widget closes, so a later tray does not keep this one alive through the shared
    // service.
    private void UnwireLook()
    {
        if (_look is { } look && _lookChanged is not null)
        {
            look.Changed -= _lookChanged;
        }

        if (_lookSettingChanged is not null)
        {
            _window.SettingChanged -= _lookSettingChanged;
        }

        _look = null;
        _lookChanged = null;
        _lookSettingChanged = null;
    }

    private int _lookReappliesAsked;

    private void ReapplyCardLook()
    {
        _lookReappliesAsked++;
        _widgetCardPresenter?.ReapplyLook();
        _caseOpenCardPresenter?.ReapplyLook();
    }
}
