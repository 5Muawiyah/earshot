using Earshot.TestWindow.Core;

namespace Earshot.TestWindow.Ui;

// The one place a derived row state becomes text and a colour. T4: green is shown for nothing
// but a fully confirmed pass (DerivedRowState.IsGreen), and the text for everything else always
// carries its qualifier rather than the bare word "Passed".
internal static class RowPresenter
{
    internal static string Text(DerivedRowState state) => Copy.RowText(state);

    internal static System.Drawing.Color RowColor(DerivedRowState state) =>
        state.IsGreen ? System.Drawing.Color.Green : System.Drawing.Color.Black;
}
