namespace Earshot.Streaming;

// What the tray draws for Play from a phone, as data, so the copy and the order are asserted without a menu.
// Command says what a click asks for; None marks a sentence, which is never enabled. DeviceId is set for Play
// and Stop only, and never reaches any text.
internal sealed record StreamingMenuItem(string Text, bool Enabled, bool Checked, string? DeviceId, StreamingMenuCommand Command);

internal sealed record StreamingMenuModel(string ParentText, bool ParentEnabled, IReadOnlyList<StreamingMenuItem> Items)
{
    internal static StreamingMenuItem Sentence(string text) =>
        new(text, Enabled: false, Checked: false, DeviceId: null, StreamingMenuCommand.None);
}
