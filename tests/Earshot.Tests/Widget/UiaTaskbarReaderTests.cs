using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// A minimal fake IUIAutomationElement: only GetCachedPropertyValue is implemented, since that is all
// UiaTaskbarReader.TryReadElements calls. A plain C# class can implement a [ComImport] interface directly;
// no real COM object is involved.
internal sealed class FakeUiaElement : IUIAutomationElement
{
    private readonly Rectangle? _bounds;
    private readonly bool _offscreen;
    private readonly string? _automationId;
    private readonly string? _className;
    private readonly int _boundingRectangleHResult;
    private readonly int _classNameHResult;
    private readonly int? _processId;
    private readonly int _processIdHResult;

    // UIA_ProcessIdPropertyId, from UIAutomationClient.h (the Windows SDK 10.0.26100.0 header on this machine and Microsoft Learn's
    // "Automation Element Property Identifiers" agree on 30002). Written here on purpose, so a wrong value in the product's own
    // constant is caught by the test that compares the two.
    internal const int ProcessIdPropertyId = 30002;

    public FakeUiaElement(
        Rectangle? bounds, bool offscreen = false, string? automationId = null, int boundingRectangleHResult = 0, string? className = null, int classNameHResult = 0,
        int? processId = null, int processIdHResult = 0)
    {
        _processId = processId;
        _processIdHResult = processIdHResult;
        _bounds = bounds;
        _offscreen = offscreen;
        _automationId = automationId;
        _className = className;
        _boundingRectangleHResult = boundingRectangleHResult;
        _classNameHResult = classNameHResult;
    }

    public int SetFocus() => throw new NotSupportedException();

    public int GetRuntimeId(out int[]? runtimeId) => throw new NotSupportedException();

    public int FindFirst(int scope, IUIAutomationCondition? condition, out IUIAutomationElement? found) => throw new NotSupportedException();

    public int FindAll(int scope, IUIAutomationCondition? condition, out IUIAutomationElementArray? found) => throw new NotSupportedException();

    public int FindFirstBuildCache(int scope, IUIAutomationCondition? condition, IUIAutomationCacheRequest? cacheRequest, out IUIAutomationElement? found) => throw new NotSupportedException();

    public int FindAllBuildCache(int scope, IUIAutomationCondition? condition, IUIAutomationCacheRequest? cacheRequest, out IUIAutomationElementArray? found) => throw new NotSupportedException();

    public int BuildUpdatedCache(IUIAutomationCacheRequest? cacheRequest, out IUIAutomationElement? updated) => throw new NotSupportedException();

    public int GetCurrentPropertyValue(int propertyId, out object? value) => throw new NotSupportedException();

    public int GetCurrentPropertyValueEx(int propertyId, bool ignoreDefaultValue, out object? value) => throw new NotSupportedException();

    public int GetCachedPropertyValue(int propertyId, out object? value)
    {
        if (propertyId == UiAutomation.UIA_BoundingRectanglePropertyId)
        {
            if (_boundingRectangleHResult < 0)
            {
                value = null;
                return _boundingRectangleHResult;
            }

            value = _bounds is { } b ? new double[] { b.X, b.Y, b.Width, b.Height } : null;
            return 0;
        }

        if (propertyId == UiAutomation.UIA_IsOffscreenPropertyId)
        {
            value = _offscreen;
            return 0;
        }

        if (propertyId == UiAutomation.UIA_AutomationIdPropertyId)
        {
            value = _automationId;
            return 0;
        }

        if (propertyId == UiAutomation.UIA_ClassNamePropertyId)
        {
            value = _classNameHResult < 0 ? null : _className;
            return _classNameHResult;
        }

        if (propertyId == ProcessIdPropertyId)
        {
            value = _processIdHResult < 0 ? null : _processId;
            return _processIdHResult;
        }

        value = null;
        return 0;
    }
}

// A fake IUIAutomationElementArray whose GetElement fails with a given HRESULT at one index, and returns a
// fixed element everywhere else.
internal sealed class FakeUiaElementArray : IUIAutomationElementArray
{
    private readonly IUIAutomationElement?[] _elements;
    private readonly int _failAtIndex;
    private readonly int _failureHResult;

    public FakeUiaElementArray(IUIAutomationElement?[] elements, int failAtIndex = -1, int failureHResult = 0)
    {
        _elements = elements;
        _failAtIndex = failAtIndex;
        _failureHResult = failureHResult;
    }

    public int get_Length(out int length)
    {
        length = _elements.Length;
        return 0;
    }

    public int GetElement(int index, out IUIAutomationElement? element)
    {
        if (index == _failAtIndex)
        {
            element = null;
            return _failureHResult;
        }

        element = _elements[index];
        return 0;
    }
}

// The real execution kept for the taskbar reader. A private-desktop run (a Form of the test's own, read
// from a second MTA thread bound to the same desktop) proved correct in isolation, but was flaky when run
// in the same process after GaugeWindowTests: closing the private desktop intermittently failed with
// ERROR_BUSY (170), reproducibly, even after forcing a GC and retrying for several seconds. Rather than
// ship a test whose own cleanup is unreliable, this uses the fallback the design allows for: the same
// real UIA read, read-only, against Shell_TrayWnd on the default desktop. Nothing here shows a window,
// clicks anything, or touches a device; it only asks UI Automation what is on the real taskbar.
//
// Each real UiaTaskbarReader constructed here calls WidgetRealSurfaceGuardTests.AllowRealConstruction(RealWidgetSurface.TaskbarReader), so
// the assembly-wide guard can tell these two named, read-only real executions apart from an unnoticed real
// construction anywhere else in the suite: every TrayContext test now gets a fake reader instead
// (TrayContextTests.TrayHarness).
[TestClass]
public sealed class UiaTaskbarReaderTests
{
    private const string ShellTrayWndClass = "Shell_TrayWnd";

    // FakeUiaElement/FakeUiaElementArray below are plain C# classes, not real COM objects:
    // Marshal.ReleaseComObject throws ArgumentException against one, so every TryReadElements call driven
    // by a fake passes this no-op instead of Earshot.Audio.ComRelease.Rcw.
    private static readonly Action<object> NoRelease = static _ => { };

    [TestMethod]
    public void RealUiaTaskbarReaderReadsTheRealTaskbarReadOnly()
    {
        nint trayHandle = FindWindowW(ShellTrayWndClass, null);
        if (trayHandle == 0)
        {
            Assert.Inconclusive("No Shell_TrayWnd on this machine (a hosted runner with no taskbar): skipped, as the design allows.");
            return;
        }

        bool ok = false;
        List<Rectangle>? occupied = null;
        Rectangle? startButton = null;
        Rectangle? notificationArea = null;
        StepOutcome? failure = null;
        var stopwatch = new Stopwatch();
        ExceptionDispatchInfo? readerFailure = null;

        var readerThread = new Thread(() =>
        {
            try
            {
                var reader = new UiaTaskbarReader();
                WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.TaskbarReader);

                // The real release, not a no-op: this is the one execution kept of Marshal.ReleaseComObject
                // actually running against the real COM objects a live Shell_TrayWnd read creates (the class
                // hygiene rule for a helper a self-test fakes elsewhere).
                //
                // A warm-up read first: a local probe found the first UIA call on this machine costs far
                // more than later ones, so the timed read below measures the steady-state cost.
                reader.TryReadOccupants(trayHandle, Rectangle.Empty, Earshot.Audio.ComRelease.Rcw, out _, out _, out _);

                stopwatch.Start();
                ok = reader.TryReadOccupants(trayHandle, Rectangle.Empty, Earshot.Audio.ComRelease.Rcw, out occupied, out startButton, out notificationArea, out failure);
                stopwatch.Stop();
            }
            catch (Exception ex)
            {
                readerFailure = ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Earshot UIA reader test",
        };
        readerThread.SetApartmentState(ApartmentState.MTA);
        readerThread.Start();
        Assert.IsTrue(readerThread.Join(TimeSpan.FromSeconds(15)), "The reader thread did not finish in time.");

        readerFailure?.Throw();
        Assert.IsTrue(ok, ok ? "" : "The real UIA read failed: " + failure!.CodeName + " " + failure.Detail);
        Assert.IsNotNull(occupied);

        // The real taskbar always has at least the notification area's buttons; an empty result would
        // mean the reader found nothing at all, which is the failure this test exists to catch.
        Assert.IsGreaterThan(0, occupied!.Count, "The real taskbar must report at least one occupant.");

        // The notification area is found by the class names UI Automation gives its buttons on this machine's
        // own taskbar (SystemTray.* for the buttons, TrayNotifyWnd for the pane that spans them). The gauge's
        // right-end position is measured from it, so a taskbar that names them otherwise would leave the
        // gauge on the tray icon: this is the one place that is checked against the real thing.
        Assert.IsNotNull(notificationArea, "The real taskbar's notification area must be identified from its class names.");
        Assert.IsTrue(occupied.Any(o => o.Left == notificationArea.Value.Left), "It starts at one of the occupants.");
        Assert.IsLessThan(5000, stopwatch.ElapsedMilliseconds, "A sanity bound on the real UIA read, not a figure the product uses.");
    }

    // Earshot.Interop.UiAutomation.AutomationElementMode_Full is declared for put_AutomationElementMode's
    // vtable slot, even though nothing calls it (Earshot always leaves the mode at IUIAutomation's own
    // default): UIAutomationClient.h's AutomationElementMode enum is AutomationElementMode_None = 0,
    // AutomationElementMode_Full = 1, so a wrong value here would silently mean something else entirely
    // the day a caller actually sets it.
    //
    // Read through reflection, not compared directly: both sides of a plain Assert.AreEqual(1,
    // UiAutomation.AutomationElementMode_Full) are compile-time constants, so MSTEST0025 flags the
    // assertion once it is known at compile time to always fail (which it must, on the wrong value, to be
    // the red half of this fix).
    [TestMethod]
    public void AutomationElementModeFullMatchesTheHeaderValue()
    {
        FieldInfo field = typeof(Earshot.Interop.UiAutomation).GetField("AutomationElementMode_Full", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.AreEqual(1, field.GetRawConstantValue());
    }

    // A security review found that a GetElement (or GetCachedPropertyValue) failure partway through an
    // enumeration was silently skipped as "not an occupant" and the read continued: this fails open, since
    // a button whose properties could not be read is then treated as free space a gauge can be placed on.
    // UiaTaskbarReader.TryReadElements is the seam extracted for exactly this: a fake array cannot be made
    // to fail through the real COM pipeline, since FindAllBuildCache runs against a real Shell_TrayWnd.
    [TestMethod]
    public void AGetElementFailurePartwayThroughFailsTheWholeReadInsteadOfSkippingIt()
    {
        const int EFail = unchecked((int)0x80004005);
        var okElement = new FakeUiaElement(new Rectangle(10, 10, 20, 20));
        var array = new FakeUiaElementArray([okElement, null, okElement], failAtIndex: 1, failureHResult: EFail);

        bool ok = UiaTaskbarReader.TryReadElements(array, Rectangle.Empty, NoRelease, out List<Rectangle> occupied, out Rectangle? startButton, out StepOutcome? failure);

        Assert.IsFalse(ok, "A read failure partway through element enumeration must fail the whole read, not silently skip the element.");
        Assert.IsNotNull(failure);
        Assert.IsFalse(failure!.Ok);
        Assert.AreEqual(EFail, failure.Code);
        Assert.IsNull(startButton);
    }

    // The same class of failure, but from the bounding-rectangle read itself on an otherwise successfully
    // enumerated element, must fail the read the same way as a GetElement failure: only a successful
    // VT_EMPTY (no bounds at all, checked in the next test) means "not an occupant", never a negative
    // HRESULT.
    [TestMethod]
    public void ABoundingRectangleReadFailureFailsTheWholeRead()
    {
        const int EFail = unchecked((int)0x80004005);
        var failingElement = new FakeUiaElement(bounds: null, boundingRectangleHResult: EFail);
        var array = new FakeUiaElementArray([failingElement]);

        bool ok = UiaTaskbarReader.TryReadElements(array, Rectangle.Empty, NoRelease, out List<Rectangle> occupied, out _, out StepOutcome? failure);

        Assert.IsFalse(ok, "A negative HRESULT reading the bounding rectangle must fail the whole read, not silently skip the element.");
        Assert.IsNotNull(failure);
        Assert.AreEqual(EFail, failure!.Code);
    }

    // The genuinely documented case (BoundingRectangle reads back VT_EMPTY with a successful HRESULT
    // because the element is not currently displaying UI) must stay a normal "not an occupant", not a
    // failure: this is what distinguishes a real failure from the one legitimate empty-but-successful
    // read the fix must not also break.
    [TestMethod]
    public void ASuccessfulEmptyBoundingRectangleIsNotAFailure()
    {
        var notDisplayingUi = new FakeUiaElement(bounds: null);
        var array = new FakeUiaElementArray([notDisplayingUi]);

        bool ok = UiaTaskbarReader.TryReadElements(array, Rectangle.Empty, NoRelease, out List<Rectangle> occupied, out _, out StepOutcome? failure);

        Assert.IsTrue(ok);
        Assert.IsNull(failure);
        Assert.IsEmpty(occupied);
    }

    // The normal path still works through the new seam: a plain occupant and the start button are both
    // recorded correctly.
    [TestMethod]
    public void NormalElementsAreReadAsOccupantsAndTheStartButtonIsFound()
    {
        var plain = new FakeUiaElement(new Rectangle(0, 0, 44, 48));
        var start = new FakeUiaElement(new Rectangle(50, 0, 45, 48), automationId: "StartButton");
        var offscreen = new FakeUiaElement(new Rectangle(100, 0, 10, 10), offscreen: true);
        var array = new FakeUiaElementArray([plain, start, offscreen]);

        bool ok = UiaTaskbarReader.TryReadElements(array, Rectangle.Empty, NoRelease, out List<Rectangle> occupied, out Rectangle? startButton, out StepOutcome? failure);

        Assert.IsTrue(ok);
        Assert.IsNull(failure);
        Assert.HasCount(2, occupied, "The offscreen element must not be counted as an occupant.");
        Assert.AreEqual(new Rectangle(50, 0, 45, 48), startButton);
    }

    // The notification area is the bounding box of the elements whose class says they belong to it.
    [TestMethod]
    public void TheNotificationAreaIsTheBoundsOfTheElementsWithTrayClassNames()
    {
        var chevron = new FakeUiaElement(new Rectangle(1738, 1032, 32, 48), className: "SystemTray.NormalButton");
        var network = new FakeUiaElement(new Rectangle(1774, 1032, 24, 48), className: "SystemTray.AccentButton");
        var clock = new FakeUiaElement(new Rectangle(1834, 1032, 70, 48), className: "SystemTray.OmniButton");
        var app = new FakeUiaElement(new Rectangle(851, 1032, 44, 48), className: "Taskbar.TaskListButtonAutomationPeer");
        var array = new FakeUiaElementArray([app, chevron, network, clock]);

        bool ok = UiaTaskbarReader.TryReadElements(array, Rectangle.Empty, NoRelease, out List<Rectangle> occupied, out _, out Rectangle? area, out StepOutcome? failure);

        Assert.IsTrue(ok);
        Assert.IsNull(failure);
        Assert.HasCount(4, occupied);
        Assert.AreEqual(Rectangle.FromLTRB(1738, 1032, 1904, 1080), area, "From the chevron's left edge to the clock's right edge; the app button is not part of it.");
    }

    [TestMethod]
    public void TheLegacyTrayPaneCountsAsTheNotificationAreaToo()
    {
        var pane = new FakeUiaElement(new Rectangle(1738, 1032, 182, 48), className: "TrayNotifyWnd");
        var app = new FakeUiaElement(new Rectangle(851, 1032, 44, 48), className: "Taskbar.TaskListButtonAutomationPeer");

        UiaTaskbarReader.TryReadElements(new FakeUiaElementArray([app, pane]), Rectangle.Empty, NoRelease, out _, out _, out Rectangle? area, out _);

        Assert.AreEqual(new Rectangle(1738, 1032, 182, 48), area);
    }

    // A taskbar that names nothing the way this one does gives no notification area, and the gauge then has
    // nothing to measure its position from: the tray icon stays.
    [TestMethod]
    public void NoTrayClassNamesMeansNoNotificationArea()
    {
        var other = new FakeUiaElement(new Rectangle(1738, 1032, 32, 48), className: "SomethingElse");
        var unnamed = new FakeUiaElement(new Rectangle(1774, 1032, 24, 48));

        UiaTaskbarReader.TryReadElements(new FakeUiaElementArray([other, unnamed]), Rectangle.Empty, NoRelease, out _, out _, out Rectangle? area, out _);

        Assert.IsNull(area);
    }

    // An offscreen tray element is not part of the area (it is not displayed).
    [TestMethod]
    public void AnOffscreenTrayElementIsNotPartOfTheNotificationArea()
    {
        var shown = new FakeUiaElement(new Rectangle(1774, 1032, 24, 48), className: "SystemTray.AccentButton");
        var hidden = new FakeUiaElement(new Rectangle(1000, 1032, 24, 48), offscreen: true, className: "SystemTray.NormalButton");

        UiaTaskbarReader.TryReadElements(new FakeUiaElementArray([hidden, shown]), Rectangle.Empty, NoRelease, out _, out _, out Rectangle? area, out _);

        Assert.AreEqual(new Rectangle(1774, 1032, 24, 48), area);
    }

    // A class name that cannot be read is not known to be anything, so the read fails rather than treating the
    // element as a non-tray occupant.
    [TestMethod]
    public void AClassNameThatCannotBeReadFailsTheWholeRead()
    {
        const int EFail = unchecked((int)0x80004005);
        var element = new FakeUiaElement(new Rectangle(1774, 1032, 24, 48), classNameHResult: EFail);

        bool ok = UiaTaskbarReader.TryReadElements(new FakeUiaElementArray([element]), Rectangle.Empty, NoRelease, out _, out _, out _, out StepOutcome? failure);

        Assert.IsFalse(ok);
        Assert.AreEqual("uia:get-cached-property:class-name", failure!.Step);
    }

    // A security review found that UiaTaskbarReader never released a single COM object it obtained during a
    // read: not the per-element IUIAutomationElement (here), and not IUIAutomationElement (root),
    // IUIAutomationElementArray, IUIAutomationCacheRequest or IUIAutomationCondition in TryReadOccupants
    // (RealReleasesEveryPerReadComObjectAtLeastFour, below). Over the life of TaskbarWatcher's polling loop
    // (once a second while the widget runs) this is an unbounded RCW/native reference leak. Every element
    // GetElement returns must be released exactly once, regardless of which branch (occupant, offscreen,
    // VT_EMPTY, or a mid-enumeration failure) handles it.
    [TestMethod]
    public void EveryElementGetElementReturnsIsReleasedExactlyOnce()
    {
        var plain = new FakeUiaElement(new Rectangle(0, 0, 44, 48));
        var start = new FakeUiaElement(new Rectangle(50, 0, 45, 48), automationId: "StartButton");
        var offscreen = new FakeUiaElement(new Rectangle(100, 0, 10, 10), offscreen: true);
        var emptyBounds = new FakeUiaElement(bounds: null);
        var array = new FakeUiaElementArray([plain, start, offscreen, emptyBounds]);
        var released = new List<object>();

        bool ok = UiaTaskbarReader.TryReadElements(array, Rectangle.Empty, released.Add, out _, out _, out _);

        Assert.IsTrue(ok);
        Assert.AreEqual(4, released.Count, "Every one of the 4 elements GetElement returned must be released exactly once.");
        CollectionAssert.AreEquivalent(new object[] { plain, start, offscreen, emptyBounds }, released);
    }

    // A mid-enumeration failure must still release the element GetElement did successfully hand back before
    // the failing call: TryReadElements returns false straight away (see
    // AGetElementFailurePartwayThroughFailsTheWholeReadInsteadOfSkippingIt), but the element it already
    // holds must not leak just because the whole read then fails.
    [TestMethod]
    public void AnElementIsReleasedEvenWhenItsOwnPropertyReadFails()
    {
        const int EFail = unchecked((int)0x80004005);
        var failingElement = new FakeUiaElement(bounds: null, boundingRectangleHResult: EFail);
        var array = new FakeUiaElementArray([failingElement]);
        var released = new List<object>();

        bool ok = UiaTaskbarReader.TryReadElements(array, Rectangle.Empty, released.Add, out _, out _, out _);

        Assert.IsFalse(ok);
        Assert.AreEqual(1, released.Count, "The element must still be released even though reading its bounding rectangle failed.");
        Assert.AreSame(failingElement, released[0]);
    }

    // The real execution for the top-level COM objects TryReadOccupants itself creates (root,
    // trueCondition, cacheRequest, found): against the real Shell_TrayWnd, so this proves
    // Marshal.ReleaseComObject (wrapped here only to count the calls, still the real release underneath)
    // neither throws nor is skipped for any of the four, not just that a fake tolerates being asked.
    [TestMethod]
    public void RealReleasesEveryPerReadComObjectAtLeastFour()
    {
        nint trayHandle = FindWindowW(ShellTrayWndClass, null);
        if (trayHandle == 0)
        {
            Assert.Inconclusive("No Shell_TrayWnd on this machine (a hosted runner with no taskbar): skipped, as the design allows.");
            return;
        }

        int releaseCalls = 0;
        Action<object> countingRelease = comObject =>
        {
            Interlocked.Increment(ref releaseCalls);
            Earshot.Audio.ComRelease.Rcw(comObject);
        };

        ExceptionDispatchInfo? readerFailure = null;
        var readerThread = new Thread(() =>
        {
            try
            {
                var reader = new UiaTaskbarReader();
                WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.TaskbarReader);
                reader.TryReadOccupants(trayHandle, Rectangle.Empty, countingRelease, out _, out _, out _);
            }
            catch (Exception ex)
            {
                readerFailure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        readerThread.SetApartmentState(ApartmentState.MTA);
        readerThread.Start();
        Assert.IsTrue(readerThread.Join(TimeSpan.FromSeconds(10)), "The reader thread did not finish within 10 s.");
        readerFailure?.Throw();

        // root, trueCondition, cacheRequest, found: the four TryReadOccupants itself always creates on a
        // successful read, before any per-element releases TryReadElements adds on top.
        Assert.IsGreaterThanOrEqualTo(4, releaseCalls,
            "TryReadOccupants must release every one of its own four per-read COM objects, not just tolerate a fake tolerating zero.");
    }

    // ----- Elements of this process are never occupants -----

    private const int Own = 4001;

    [TestMethod]
    public void AnElementOfTheGivenProcessIsNotAnOccupantAndAnotherProcessesElementIs()
    {
        var gauge = new FakeUiaElement(new Rectangle(1596, 1036, 74, 40), className: "WindowsForms10.Window.8.app.0.1_r1_ad1", processId: Own);
        var button = new FakeUiaElement(new Rectangle(807, 1032, 44, 48), processId: 5000);
        var noProcess = new FakeUiaElement(new Rectangle(851, 1032, 44, 48));
        var array = new FakeUiaElementArray([gauge, button, noProcess]);

        bool ok = UiaTaskbarReader.TryReadElements(array, Rectangle.Empty, NoRelease, Own, out List<Rectangle> occupied, out _, out _, out StepOutcome? failure);

        Assert.IsTrue(ok, failure?.CodeName);
        CollectionAssert.AreEqual(new[] { new Rectangle(807, 1032, 44, 48), new Rectangle(851, 1032, 44, 48) }, occupied, "An element with no process id is not known to be ours.");
    }

    [TestMethod]
    public void AnElementOfTheGivenProcessIsNeitherTheStartButtonNorPartOfTheNotificationArea()
    {
        var ownStart = new FakeUiaElement(new Rectangle(0, 0, 40, 40), automationId: "StartButton", processId: Own);
        var ownTray = new FakeUiaElement(new Rectangle(1000, 0, 40, 40), className: "SystemTray.NormalButton", processId: Own);
        var tray = new FakeUiaElement(new Rectangle(1800, 0, 40, 40), className: "SystemTray.NormalButton", processId: 5000);

        bool ok = UiaTaskbarReader.TryReadElements(
            new FakeUiaElementArray([ownStart, ownTray, tray]), Rectangle.Empty, NoRelease, Own, out _, out Rectangle? start, out Rectangle? area, out _);

        Assert.IsTrue(ok);
        Assert.IsNull(start);
        Assert.AreEqual(new Rectangle(1800, 0, 40, 40), area);
    }

    // The overloads production calls name no process: they mean this one.
    [TestMethod]
    public void TheReadWithNoProcessGivenSkipsTheElementsOfThisProcess()
    {
        var gauge = new FakeUiaElement(new Rectangle(1596, 1036, 74, 40), processId: Environment.ProcessId);
        var button = new FakeUiaElement(new Rectangle(807, 1032, 44, 48), processId: 5000);

        bool ok = UiaTaskbarReader.TryReadElements(new FakeUiaElementArray([gauge, button]), Rectangle.Empty, NoRelease, out List<Rectangle> occupied, out _, out _);

        Assert.IsTrue(ok);
        CollectionAssert.AreEqual(new[] { new Rectangle(807, 1032, 44, 48) }, occupied);
    }

    [TestMethod]
    public void AProcessIdThatCannotBeReadFailsTheWholeRead()
    {
        const int EFail = unchecked((int)0x80004005);
        var element = new FakeUiaElement(new Rectangle(1774, 1032, 24, 48), processIdHResult: EFail);

        bool ok = UiaTaskbarReader.TryReadElements(new FakeUiaElementArray([element]), Rectangle.Empty, NoRelease, Own, out _, out _, out _, out StepOutcome? failure);

        Assert.IsFalse(ok, "An element whose process is not known to be ours is not known to be free space either.");
        Assert.AreEqual("uia:get-cached-property:process-id", failure!.Step);
        Assert.AreEqual(EFail, failure.Code);
    }

    // Read through reflection for the reason AutomationElementModeFullMatchesTheHeaderValue gives.
    [TestMethod]
    public void TheProcessIdPropertyMatchesTheHeaderValue()
    {
        FieldInfo field = typeof(Earshot.Interop.UiAutomation).GetField("UIA_ProcessIdPropertyId", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.AreEqual(FakeUiaElement.ProcessIdPropertyId, field.GetRawConstantValue());
    }

    // The boundary itself, with real windows: a window owned by another is listed among its owner's descendants by UI Automation,
    // which is how the gauge (the taskbar's owned window) came to be read as an occupant of the bar it sits on. Two forms of this
    // process on a private desktop, the second owned by the first the way the gauge is owned; the real reader reads the first
    // several times. Told no process is its own it finds the owned form's rectangle; told this one it does not.
    [TestMethod]
    public void TheRealReaderFindsAnOwnedWindowAmongItsOwnersDescendantsUnlessItIsToldTheWindowIsItsOwn()
    {
        Phase5.CardDesktop.Run(desktop =>
        {
            using var owner = new Form { Text = "stability-owner", StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(100, 100, 600, 300), ShowInTaskbar = false };
            using var owned = new Form
            {
                Text = "stability-owned", FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
                Bounds = new Rectangle(300, 200, 74, 40),
            };
            owner.Show();
            owned.Show();
            _ = NativeMethods.SetWindowLongPtrW(owned.Handle, NativeMethods.GWLP_HWNDPARENT, owner.Handle);
            Application.DoEvents();
            Assert.IsTrue(NativeMethods.GetWindowRect(owned.Handle, out RECT ownedRect));
            Rectangle ownedBounds = Rectangle.FromLTRB(ownedRect.left, ownedRect.top, ownedRect.right, ownedRect.bottom);

            List<Rectangle> unfiltered = ReadOwnerOnItsOwnThread(owner.Handle, desktop, ownProcessId: 0, out StepOutcome? unfilteredFailure);
            Assert.IsNull(unfilteredFailure, unfilteredFailure?.CodeName + " " + unfilteredFailure?.Detail);
            List<Rectangle> filtered = ReadOwnerOnItsOwnThread(owner.Handle, desktop, Environment.ProcessId, out StepOutcome? filteredFailure);
            Assert.IsNull(filteredFailure, filteredFailure?.CodeName + " " + filteredFailure?.Detail);

            Assert.IsTrue(
                unfiltered.Any(r => Near(r, ownedBounds)),
                "Before the filter the owned form is listed among the owner's descendants: " + string.Join(" ", unfiltered) + " owned=" + ownedBounds);
            Assert.IsFalse(
                filtered.Any(r => Near(r, ownedBounds)),
                "Told this process's windows are its own, the reader does not count the owned form: " + string.Join(" ", filtered));
            Assert.IsLessThan(unfiltered.Count, filtered.Count);

            owned.Hide();
            Application.DoEvents();
            List<Rectangle> hidden = ReadOwnerOnItsOwnThread(owner.Handle, desktop, ownProcessId: 0, out _);
            Assert.IsFalse(hidden.Any(r => Near(r, ownedBounds)), "A hidden owned window is not listed, which is what made the gauge flip.");
        });
    }

    private static bool Near(Rectangle a, Rectangle b) =>
        Math.Abs(a.X - b.X) <= 2 && Math.Abs(a.Y - b.Y) <= 2 && Math.Abs(a.Width - b.Width) <= 2 && Math.Abs(a.Height - b.Height) <= 2;

    // The real reader on a thread of its own (UI Automation must be called from a thread that owns no window), bound to the
    // desktop the forms are on, while this thread keeps pumping so the forms answer UI Automation.
    private static List<Rectangle> ReadOwnerOnItsOwnThread(nint ownerHandle, nint desktop, int ownProcessId, out StepOutcome? failure)
    {
        List<Rectangle>? occupied = null;
        StepOutcome? readFailure = null;
        ExceptionDispatchInfo? thrown = null;
        var thread = new Thread(() =>
        {
            try
            {
                Phase5.CardDesktop.BindCurrentThread(desktop);
                var reader = new UiaTaskbarReader();
                WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.TaskbarReader);
                bool ok = reader.TryReadOccupants(
                    ownerHandle, Rectangle.Empty, Earshot.Audio.ComRelease.Rcw, ownProcessId, out List<Rectangle> found, out _, out _, out StepOutcome? problem);
                occupied = ok ? found : null;
                readFailure = problem;
            }
            catch (Exception ex)
            {
                thrown = ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Earshot UIA owned-window test",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        var watch = Stopwatch.StartNew();
        while (!thread.Join(10))
        {
            Application.DoEvents();
            Assert.IsLessThan(20_000, watch.ElapsedMilliseconds, "The reader did not finish in time.");
        }

        thrown?.Throw();
        failure = readFailure;
        return occupied ?? [];
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindWindowW(string lpClassName, string? lpWindowName);
}
