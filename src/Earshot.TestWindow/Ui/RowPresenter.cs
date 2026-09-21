using Earshot.TestWindow.Core;

namespace Earshot.TestWindow.Ui;

// The one place a derived row state becomes text and a colour. Green is shown for nothing
// but a fully confirmed pass (DerivedRowState.IsGreen), and the text for everything else always
// carries its qualifier rather than the bare word "Passed".
internal static class RowPresenter
{
    internal static string Text(DerivedRowState state) => Copy.RowText(state);

    // Shown with technical details off: the same state, in plain words (Copy.PlainRowText). The
    // colour rule is unaffected either way: green is still shown for nothing but a fully
    // confirmed clean pass.
    internal static string PlainText(DerivedRowState state) => Copy.PlainRowText(state);

    internal static System.Drawing.Color RowColor(DerivedRowState state) =>
        state.IsGreen ? System.Drawing.Color.Green : System.Drawing.Color.Black;
}
