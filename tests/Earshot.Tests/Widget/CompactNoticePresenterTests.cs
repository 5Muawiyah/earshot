using System.Drawing;
using System.Text.RegularExpressions;
using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Tests.TestWindow;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Icon = Earshot.Icons.Catalogue.Icon;

namespace Earshot.Tests.Widget;

// The ICardPresenter behind registry.Cards: what a CardContent becomes as a compact notice, and what happens with no card to show it.
[TestClass]
public sealed class CompactNoticePresenterTests
{
    private sealed class RecordingTarget : ICompactNoticeTarget
    {
        public List<CompactRequest> Requests { get; } = new();

        public int Hides { get; private set; }

        public bool Answer { get; set; } = true;

        public Task<bool> ShowCompact(CompactRequest request)
        {
            Requests.Add(request);
            return Task.FromResult(Answer);
        }

        public void HideCompact() => Hides++;
    }

    private static readonly string[] ThreeNotices = ["One", "Two", "Three"];

    private const string Reason = "The AirPods did not answer. Check them and try again.";

    [TestMethod]
    public void ACardContentWithAnActionIsATextButtonThatStaysTwelveSeconds()
    {
        int ran = 0;
        CompactRequest request = CompactNoticePresenter.RequestFor(
            new CardContent("Earshot", Reason, new CardAction("Fix connection", () => ran++)), CardAnchor.NearTray, null, "Pods");

        Assert.AreEqual(Reason, request.Content.Text, "The app's name is not repeated on the row.");
        Assert.AreEqual("Fix connection", request.Content.Action!.Label);
        Assert.IsFalse(request.Content.Action.IsIcon, "A notice's button is a text button.");
        Assert.AreEqual(TimeSpan.FromSeconds(12), request.Content.Hold);
        Assert.AreEqual(UnaskedKind.Notice, request.Content.Kind);
        Assert.IsNull(request.Content.Glyph, "A notice has no glyph unless the caller's content has one.");
        request.OnAction!();
        Assert.AreEqual(1, ran);
    }

    [TestMethod]
    public void ACardContentWithNoActionStaysSixSeconds()
    {
        CompactRequest request = CompactNoticePresenter.RequestFor(new CardContent("Earshot", "Disconnected"), CardAnchor.NearTray, null, null);

        Assert.AreEqual(TimeSpan.FromSeconds(6), request.Content.Hold);
        Assert.IsNull(request.Content.Action);
        Assert.IsNull(request.OnAction);
    }

    [TestMethod]
    public void AContentThatStaysUpHasNoHold()
    {
        CompactRequest request = CompactNoticePresenter.RequestFor(
            new CardContent("Earshot", "Recording the trace.", new CardAction("Stop", () => { }), StaysUp: true), CardAnchor.NearTray, null, null);

        Assert.IsNull(request.Content.Hold, "It stays until Hide.");
        Assert.AreEqual("Stop", request.Content.Action!.Label);
    }

    [TestMethod]
    public void TheTitleIsDroppedWhenItIsTheAppsNameOrTheDevicesAndKeptOtherwise()
    {
        Assert.AreEqual("Done", CompactNoticePresenter.RequestFor(new CardContent("Earshot", "Done"), CardAnchor.NearTray, null, "Pods").Content.Text);
        Assert.AreEqual("Done", CompactNoticePresenter.RequestFor(new CardContent("Pods", "Done"), CardAnchor.NearTray, null, "Pods").Content.Text);
        Assert.AreEqual("Update available: Version 2 is ready.", CompactNoticePresenter.RequestFor(
            new CardContent("Update available", "Version 2 is ready."), CardAnchor.NearTray, null, "Pods").Content.Text);
    }

    [TestMethod]
    public void TheClickAndTheAnchorTravelWithTheRequest()
    {
        CompactRequest request = CompactNoticePresenter.RequestFor(new CardContent("Earshot", "x"), CardAnchor.NearCursor, new Point(5, 6), null);

        Assert.AreEqual(CardAnchor.NearCursor, request.Anchor);
        Assert.AreEqual(new Point(5, 6), request.ClickPoint);
    }

    [TestMethod]
    public async Task WithNoCardYetANoticeIsLoggedAndDroppedAndShowAsyncIsFalse()
    {
        var log = new CapturingLog();
        var presenter = new CompactNoticePresenter(log);

        presenter.Show(new CardContent("Earshot", "Hello"), CardAnchor.NearTray);
        bool shown = await presenter.ShowAsync(new CardContent("Earshot", "Hello"), CardAnchor.NearCursor, new Point(1, 1));
        presenter.Hide();

        Assert.IsFalse(shown);
        Assert.IsTrue(log.Has(LogLevel.Debug, "Card: not shown (notice), there is no card yet."));
        Assert.IsFalse(log.Entries.Any(e => e.Message.Contains("Hello", StringComparison.Ordinal)), "A notice's text is not logged.");
    }

    [TestMethod]
    public async Task ShowShowAsyncAndHideGoToTheTargetOnceItIsSet()
    {
        var target = new RecordingTarget();
        var presenter = new CompactNoticePresenter(new CapturingLog());
        presenter.SetTarget(target);
        presenter.SetDeviceName(() => "Pods");

        presenter.Show(new CardContent("Pods", "One"), CardAnchor.NearTray);
        presenter.Show(new CardContent("Earshot", "Two"), CardAnchor.NearCursor, new Point(3, 4));
        bool shown = await presenter.ShowAsync(new CardContent("Earshot", "Three"), CardAnchor.NearTray, null);
        presenter.Hide();

        Assert.IsTrue(shown);
        CollectionAssert.AreEqual(ThreeNotices, target.Requests.Select(r => r.Content.Text).ToArray());
        Assert.AreEqual(new Point(3, 4), target.Requests[1].ClickPoint);
        Assert.AreEqual(1, target.Hides);

        presenter.SetTarget(null);
        Assert.IsFalse(await presenter.ShowAsync(new CardContent("Earshot", "Four"), CardAnchor.NearTray, null));
    }

    [TestMethod]
    public async Task AnAnswerOfHeldBackIsPassedBack()
    {
        var presenter = new CompactNoticePresenter(new CapturingLog());
        presenter.SetTarget(new RecordingTarget { Answer = false });

        Assert.IsFalse(await presenter.ShowAsync(new CardContent("Earshot", "x"), CardAnchor.NearTray, null));
    }

    [TestMethod]
    public void TheCompositionWiresTheNoticePresenter()
    {
        var log = new CapturingLog();
        using var temp = new TempFolder();

        ServiceRegistry registry = CompositionRoot.Build(log, new JsonSettingsStore(temp.File("settings.json"), log), a => a(), safeMode: true);
        try
        {
            Assert.IsInstanceOfType<CompactNoticePresenter>(registry.Cards);
            Assert.IsFalse(((CompactNoticePresenter)registry.Cards).HasTarget, "No card exists until the tray makes one.");
        }
        finally
        {
            (((SafeBlockController)registry.Block).Inner as IDisposable)?.Dispose();
            registry.Monitor.Dispose();
            registry.Worker?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    // Every window the app shows is the one card, the gauge, or an answer to something he did (a picker, a name form, Fix
    // connection's own OKs). A new Form that shows a status would be a second popup.
    [TestMethod]
    public void NoFormIsShownFromOutsideTheAllowedList()
    {
        string src = Path.Combine(RepositoryLocator.RepositoryRoot(), "src", "Earshot");
        string[] allowed =
        [
            "WidgetCard.cs", "GaugeWindow.cs", "SecondaryGauge.cs", "SendToPicker.cs", "OtherDeviceNameForm.cs",
            "FixConnectionUi.cs", "PicturePrompt.cs", "DevicePickerForm.cs",
        ];
        var shows = new Regex(@"(class\s+\w+[^{;]*:\s*(System\.Windows\.Forms\.)?Form\b|new\s+(System\.Windows\.Forms\.)?Form\b)", RegexOptions.CultureInvariant);

        var found = new List<string>();
        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(src, file);
            if (relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) || relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            if (shows.IsMatch(File.ReadAllText(file)))
            {
                found.Add(Path.GetFileName(file));
            }
        }

        string[] outside = found.Where(name => !allowed.Contains(name, StringComparer.Ordinal)).ToArray();
        Assert.IsEmpty(outside, "A Form outside the allowed list: " + string.Join(", ", outside));
        Assert.IsTrue(found.Contains("WidgetCard.cs"), "Sanity: the scan found the one card.");
    }
}
