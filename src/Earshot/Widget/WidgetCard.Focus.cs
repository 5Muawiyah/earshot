namespace Earshot.Widget;

// When the card shows its focus visual: only for keyboard use. The card takes the keyboard focus the moment it
// opens, so focus alone says nothing about how the owner got here; the cue says whether the keyboard is in use.
internal sealed partial class WidgetCard
{
    private readonly KeyboardFocusCue _cue = new();

    // True while the focus visual is drawn: the card holds the focus and the keyboard is the way it is being used.
    private bool FocusShown => ContainsFocus && _cue.Visible;

    // Whether the keyboard cue is on, for tests.
    internal bool FocusCueVisible => _cue.Visible;

    // Called when the card opens: true when it was opened from the keyboard, so the visual shows from the start.
    internal void ResetFocusCue(bool openedByKeyboard)
    {
        _cue.Reset(openedByKeyboard);
        Invalidate();
    }

    private void NoteKeyForFocusCue(Keys keyData)
    {
        bool before = _cue.Visible;
        _cue.KeyDown(keyData);
        if (_cue.Visible != before)
        {
            Invalidate();
        }
    }

    private void NoteMouseForFocusCue()
    {
        bool before = _cue.Visible;
        _cue.MouseDown();
        if (_cue.Visible != before)
        {
            Invalidate();
        }
    }
}
