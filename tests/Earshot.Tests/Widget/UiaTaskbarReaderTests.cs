using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
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
    private readonly int _boundingRectangleHResult;

    public FakeUiaElement(Rectangle? bounds, bool offscreen = false, string? automationId = null, int boundingRectangleHResult = 0)
    {
        _bounds = bounds;
        _offscreen = offscreen;
        _automationId = automationId;
        _boundingRectangleHResult = boundingRectangleHResult;
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
[TestClass]
public sealed class UiaTaskbarReaderTests
{
    private const string ShellTrayWndClass = "Shell_TrayWnd";

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
        StepOutcome? failure = null;
        var stopwatch = new Stopwatch();
        ExceptionDispatchInfo? readerFailure = null;

        var readerThread = new Thread(() =>
        {
            try
            {
                var reader = new UiaTaskbarReader();

                // A warm-up read first: a local probe found the first UIA call on this machine costs far
                // more than later ones, so the timed read below measures the steady-state cost.
                reader.TryReadOccupants(trayHandle, Rectangle.Empty, out _, out _, out _);

                stopwatch.Start();
                ok = reader.TryReadOccupants(trayHandle, Rectangle.Empty, out occupied, out startButton, out failure);
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

        bool ok = UiaTaskbarReader.TryReadElements(array, Rectangle.Empty, out List<Rectangle> occupied, out Rectangle? startButton, out StepOutcome? failure);

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

        bool ok = UiaTaskbarReader.TryReadElements(array, Rectangle.Empty, out List<Rectangle> occupied, out _, out StepOutcome? failure);

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

        bool ok = UiaTaskbarReader.TryReadElements(array, Rectangle.Empty, out List<Rectangle> occupied, out _, out StepOutcome? failure);

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

        bool ok = UiaTaskbarReader.TryReadElements(array, Rectangle.Empty, out List<Rectangle> occupied, out Rectangle? startButton, out StepOutcome? failure);

        Assert.IsTrue(ok);
        Assert.IsNull(failure);
        Assert.HasCount(2, occupied, "The offscreen element must not be counted as an occupant.");
        Assert.AreEqual(new Rectangle(50, 0, 45, 48), startButton);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindWindowW(string lpClassName, string? lpWindowName);
}
