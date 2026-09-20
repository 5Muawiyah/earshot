namespace Earshot.TestWindow.Ui;

// The window's shell. Empty at this slice: the manifest, the rows and the child process driver
// arrive in later slices. Forms hold no decisions (design.md section 5); this one holds nothing
// yet at all.
internal sealed class MainForm : Form
{
    internal MainForm()
    {
        Text = "Earshot live tests";
        Width = 900;
        Height = 600;
        StartPosition = FormStartPosition.CenterScreen;
    }
}
