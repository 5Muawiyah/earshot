using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

[TestClass]
public sealed class SystemLookServiceTests
{
    private sealed class FakeSource : ISystemLookSource
    {
        public double TextScaleFactor { get; set; } = 1.0;

        public bool AdvancedEffectsEnabled { get; set; } = true;

        public int Disposed { get; private set; }

        public event EventHandler? Changed;

        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);

        public bool HasSubscribers => Changed is not null;

        public void Dispose() => Disposed++;
    }

    private static SystemLookService Service(FakeSource source, Func<bool>? highContrast = null, List<Action>? posted = null) =>
        new(source, highContrast ?? (() => false), posted is null ? static a => a() : posted.Add, new CapturingLog());

    [TestMethod]
    public void TheCurrentLookIsReadFromTheSourceAndClamped()
    {
        var source = new FakeSource { TextScaleFactor = 1.5, AdvancedEffectsEnabled = false };
        using SystemLookService service = Service(source, () => true);
        Assert.AreEqual(new SystemLook(1.5, Transparency: false, HighContrast: true), service.Current);

        source.TextScaleFactor = 9;
        Assert.AreEqual(2.25, service.Current.TextScale, "Windows offers up to 225%.");
        source.TextScaleFactor = 0.2;
        Assert.AreEqual(1.0, service.Current.TextScale);
        source.TextScaleFactor = double.NaN;
        Assert.AreEqual(1.0, service.Current.TextScale);
    }

    [TestMethod]
    public void ASourceChangeIsPostedToTheUiThreadAndRaisedOncePerRealChange()
    {
        var source = new FakeSource();
        var posted = new List<Action>();
        using SystemLookService service = Service(source, posted: posted);
        int raised = 0;
        service.Changed += (_, _) => raised++;

        source.TextScaleFactor = 1.25;
        source.Raise();
        Assert.AreEqual(0, raised, "Nothing runs on the Windows thread.");
        Assert.AreEqual(1, posted.Count);
        posted[0]();
        Assert.AreEqual(1, raised);

        source.Raise();
        posted[1]();
        Assert.AreEqual(1, raised, "No change in what is read, no event.");
    }

    [TestMethod]
    public void PokingNoticesAChangeTheSourceNeverAnnounced()
    {
        var source = new FakeSource();
        bool highContrast = false;
        using SystemLookService service = Service(source, () => highContrast);
        int raised = 0;
        service.Changed += (_, _) => raised++;

        service.Poke();
        Assert.AreEqual(0, raised);
        highContrast = true;
        service.Poke();
        Assert.AreEqual(1, raised, "A high-contrast switch arrives as a settings change message, not a source event.");
        service.Poke();
        Assert.AreEqual(1, raised);
    }

    [TestMethod]
    public void TransparencyOffOrHighContrastMeansAnOpaqueBackground()
    {
        Assert.IsFalse(new SystemLook(1.0, Transparency: true, HighContrast: false).OpaqueBackground);
        Assert.IsTrue(new SystemLook(1.0, Transparency: false, HighContrast: false).OpaqueBackground);
        Assert.IsTrue(new SystemLook(1.0, Transparency: true, HighContrast: true).OpaqueBackground);
    }

    [TestMethod]
    public void DisposingLetsTheSourceGoAndSilencesLaterEvents()
    {
        var source = new FakeSource();
        SystemLookService service = Service(source);
        int raised = 0;
        service.Changed += (_, _) => raised++;
        service.Dispose();
        service.Dispose();

        Assert.AreEqual(1, source.Disposed);
        Assert.IsFalse(source.HasSubscribers);
        source.TextScaleFactor = 2;
        service.Poke();
        Assert.AreEqual(0, raised);
    }

    [TestMethod]
    public void TheRealSourceReadsTheSystemsOwnValuesWithinTheirRange()
    {
        using var real = new UiSettingsLookSource();
        Assert.IsTrue(real.TextScaleFactor is >= 1.0 and <= 2.25, "UISettings.TextScaleFactor runs from 1 to 2.25, read " + real.TextScaleFactor);
        _ = real.AdvancedEffectsEnabled;
    }
}
