using System.Drawing;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Earshot.Tests.Widget;

// The accent colour service: the right shade for each theme, a change delivered on the UI thread, and one real
// read of Windows' own palette checked against the palette the registry records.
[TestClass]
public sealed class AccentColourServiceTests
{
    private sealed class FakeColourSource : IUiColourSource
    {
        public Color Dark1 { get; set; } = Color.FromArgb(0x00, 0x5F, 0xB8);

        public Color Light2 { get; set; } = Color.FromArgb(0x60, 0xCD, 0xFF);

        public bool Disposed { get; private set; }

        public event EventHandler? ColorValuesChanged;

        public Color Shade(AccentShade shade) => shade == AccentShade.Dark1 ? Dark1 : Light2;

        public void Raise() => ColorValuesChanged?.Invoke(this, EventArgs.Empty);

        public void Dispose() => Disposed = true;
    }

    private static (AccentColourService Service, FakeColourSource Source, Queue<Action> Posted) Build()
    {
        var source = new FakeColourSource();
        var posted = new Queue<Action>();
        return (new AccentColourService(source, posted.Enqueue, new CapturingLog()), source, posted);
    }

    [TestMethod]
    public void TheLightThemeUsesTheDark1ShadeAndTheDarkThemeUsesTheLight2Shade()
    {
        (AccentColourService service, FakeColourSource source, _) = Build();

        Assert.AreEqual(source.Dark1, service.AccentFor(lightTheme: true));
        Assert.AreEqual(source.Light2, service.AccentFor(lightTheme: false));
    }

    // With the default Windows blue, those two shades are the design's own #005FB8 and #60CDFF.
    [TestMethod]
    public void TheDefaultBlueGivesTheDesignsTwoColours()
    {
        Assert.AreEqual(Color.FromArgb(0x00, 0x5F, 0xB8).ToArgb(), UiSettingsColourSource.DefaultShade(AccentShade.Dark1).ToArgb());
        Assert.AreEqual(Color.FromArgb(0x60, 0xCD, 0xFF).ToArgb(), UiSettingsColourSource.DefaultShade(AccentShade.Light2).ToArgb());
    }

    [TestMethod]
    public void AChangedColourIsReadAtOnceOnTheNextAsk()
    {
        (AccentColourService service, FakeColourSource source, _) = Build();
        Color before = service.AccentFor(true);

        source.Dark1 = Color.FromArgb(200, 30, 90);

        Assert.AreNotEqual(before, service.AccentFor(true));
        Assert.AreEqual(Color.FromArgb(200, 30, 90), service.AccentFor(true));
    }

    [TestMethod]
    public void AChangeEventIsPostedToTheUiThreadNotRaisedOnTheCallersThread()
    {
        (AccentColourService service, FakeColourSource source, Queue<Action> posted) = Build();
        int raised = 0;
        service.Changed += (_, _) => raised++;

        source.Raise();

        Assert.AreEqual(0, raised, "Nothing is raised on the Windows thread that reported the change.");
        Assert.HasCount(1, posted);
        posted.Dequeue()();
        Assert.AreEqual(1, raised);
    }

    [TestMethod]
    public void AChangeReportedFromAnotherThreadStillReachesTheUiPost()
    {
        (AccentColourService service, FakeColourSource source, Queue<Action> posted) = Build();
        int raised = 0;
        service.Changed += (_, _) => raised++;

        var thread = new Thread(source.Raise);
        thread.Start();
        thread.Join();

        Assert.HasCount(1, posted);
        posted.Dequeue()();
        Assert.AreEqual(1, raised);
    }

    [TestMethod]
    public void AfterDisposeNoChangeIsPostedAndAnAlreadyPostedOneIsDropped()
    {
        (AccentColourService service, FakeColourSource source, Queue<Action> posted) = Build();
        int raised = 0;
        service.Changed += (_, _) => raised++;
        source.Raise();

        service.Dispose();
        posted.Dequeue()();
        source.Raise();

        Assert.AreEqual(0, raised);
        Assert.IsEmpty(posted);
        Assert.IsTrue(source.Disposed);
    }

    // The one real execution: Windows' own palette through the real UISettings, compared with the palette the
    // registry keeps (entries in order: light 3, light 2, light 1, accent, dark 1, dark 2, dark 3). The
    // registry value is undocumented, so it is read only here as a witness, never in the product.
    [TestMethod]
    public void TheRealColourSourceAgreesWithTheRegistryPalette()
    {
        byte[]? palette = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent", "AccentPalette", null) as byte[];
        if (palette is not { Length: >= 32 })
        {
            Assert.Inconclusive("This account has no AccentPalette value to compare with.");
            return;
        }

        using var source = new UiSettingsColourSource();
        Color dark1 = source.Shade(AccentShade.Dark1);
        Color light2 = source.Shade(AccentShade.Light2);

        Assert.AreEqual(Color.FromArgb(palette[16], palette[17], palette[18]).ToArgb() | unchecked((int)0xFF000000), dark1.ToArgb(), "Dark 1 is entry 4 of the palette.");
        Assert.AreEqual(Color.FromArgb(palette[4], palette[5], palette[6]).ToArgb() | unchecked((int)0xFF000000), light2.ToArgb(), "Light 2 is entry 1 of the palette.");
    }
}
