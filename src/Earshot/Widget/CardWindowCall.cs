namespace Earshot.Widget;

// What kind of call a card made on its own window or its own paint, for the recording seam (WidgetCard.CallRecorder).
internal enum CardWindowCallKind
{
    // The card was placed at rest (PlaceAtRest) or shown at rest.
    Place,

    // The client size was set to a size it did not have.
    ClientSize,

    // The motion's move call (SetWindowPos with no resize and no z-order change).
    Move,

    // The motion's alpha call (SetLayeredWindowAttributes).
    Alpha,

    // DwmSetWindowAttribute or DwmExtendFrameIntoClientArea on the card's window.
    DwmAttribute,

    // The window was invalidated: Area is the rectangle (the whole client for a full invalidation).
    Invalidate,
}

// One recorded call. Pure data, so the stall log and its tests need no window.
internal readonly record struct CardWindowCall(CardWindowCallKind Kind, Rectangle Area, string Detail)
{
    public override string ToString() =>
        Kind + " " + Detail + (Area.IsEmpty ? "" : " " + Area.X + "," + Area.Y + " " + Area.Width + "x" + Area.Height);
}
