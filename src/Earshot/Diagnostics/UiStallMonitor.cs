using System.Globalization;
using System.Runtime.InteropServices;
using Earshot.Widget;

namespace Earshot.Diagnostics;

// A fixed-size ring of text lines, oldest dropped first. Thread safe: the UI thread writes, the thread that builds the
// diagnostics text reads.
internal sealed class FrameLogRing(int capacity)
{
    public const int DefaultCapacity = 256; // design choice: a few minutes of stalls and card calls, small enough to paste

    private readonly string[] _lines = new string[capacity];
    private readonly Lock _gate = new();
    private int _next;
    private int _count;

    public int Capacity => _lines.Length;

    public void Add(string line)
    {
        lock (_gate)
        {
            _lines[_next] = line;
            _next = (_next + 1) % _lines.Length;
            _count = Math.Min(_count + 1, _lines.Length);
        }
    }

    // Oldest first.
    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            var result = new List<string>(_count);
            int start = (_next - _count + _lines.Length) % _lines.Length;
            for (int i = 0; i < _count; i++)
            {
                result.Add(_lines[(start + i) % _lines.Length]);
            }

            return result;
        }
    }
}

// The display's refresh interval, from the compositor's own timing.
// https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmgetcompositiontiminginfo
internal static class RefreshInterval
{
    // A FALLBACK, not a measurement: 1000 ms / 60 Hz, used only when DwmGetCompositionTimingInfo cannot be read.
    public static readonly TimeSpan Fallback = TimeSpan.FromMilliseconds(1000.0 / 60.0);

    // The interval and where it came from: "DwmGetCompositionTimingInfo" or "fallback 16.7 ms (HRESULT 0x...)". The raw
    // HRESULT of a failed call is kept in the source text, never dropped.
    public static (TimeSpan Interval, string Source) Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (Fallback, "fallback 16.7 ms (not Windows)");
        }

        var info = new DwmTimingInfo { cbSize = (uint)Marshal.SizeOf<DwmTimingInfo>() };
        int hr = DwmGetCompositionTimingInfo(0, ref info);
        if (hr < 0)
        {
            return (Fallback, "fallback 16.7 ms (DwmGetCompositionTimingInfo HRESULT 0x" + hr.ToString("X8", CultureInfo.InvariantCulture) + ")");
        }

        if (info.rateRefreshDenominator == 0 || info.rateRefreshNumerator == 0)
        {
            return (Fallback, "fallback 16.7 ms (DwmGetCompositionTimingInfo reported no refresh rate)");
        }

        // rateRefresh is the refresh rate as a ratio of frames per second.
        return (TimeSpan.FromSeconds((double)info.rateRefreshDenominator / info.rateRefreshNumerator), "DwmGetCompositionTimingInfo");
    }

    [DllImport("dwmapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DwmGetCompositionTimingInfo(nint hwnd, ref DwmTimingInfo info);

    // DWM_TIMING_INFO (dwmapi.h), every field in its documented order; only the refresh rate is read.
    [StructLayout(LayoutKind.Sequential)]
    private struct DwmTimingInfo
    {
        public uint cbSize;
        public uint rateRefreshNumerator;
        public uint rateRefreshDenominator;
        public ulong qpcRefreshPeriod;
        public uint rateComposeNumerator;
        public uint rateComposeDenominator;
        public ulong qpcVBlank;
        public ulong cRefresh;
        public uint cDXRefresh;
        public ulong qpcCompose;
        public ulong cFrame;
        public uint cDXPresent;
        public ulong cRefreshFrame;
        public ulong cFrameSubmitted;
        public uint cDXPresentSubmitted;
        public ulong cFrameConfirmed;
        public uint cDXPresentConfirmed;
        public ulong cRefreshConfirmed;
        public uint cDXRefreshConfirmed;
        public ulong cFramesLate;
        public uint cFramesOutstanding;
        public ulong cFrameDisplayed;
        public ulong qpcFrameDisplayed;
        public ulong cRefreshFrameDisplayed;
        public ulong cFrameComplete;
        public ulong qpcFrameComplete;
        public ulong cFramePending;
        public ulong qpcFramePending;
        public ulong cFramesDisplayed;
        public ulong cFramesComplete;
        public ulong cFramesPending;
        public ulong cFramesAvailable;
        public ulong cFramesDropped;
        public ulong cFramesMissed;
        public ulong cRefreshNextDisplayed;
        public ulong cRefreshNextPresented;
        public ulong cRefreshesDisplayed;
        public ulong cRefreshesPresented;
        public ulong cRefreshStarted;
        public ulong cPixelsReceived;
        public ulong cPixelsDrawn;
        public ulong cBuffersEmpty;
    }
}

// The frame and UI stall log. Wraps the actions posted to the UI thread (Wrap): one that waits in the queue, or runs, for
// longer than two refresh intervals (a design choice: two missed frames is a hitch the eye can see) is recorded with the
// action's method name. The card's window calls (CardWindowCall, from WidgetCard.CallRecorder) go in the same ring beside
// them, so a hitch can be read against what the card did around it. Copy diagnostics prints it (IFrameLogSource).
internal sealed class UiStallMonitor : IFrameLogSource
{
    private readonly TimeProvider _time;
    private readonly FrameLogRing _ring = new(FrameLogRing.DefaultCapacity);
    private readonly string _header;

    public UiStallMonitor(TimeProvider time, TimeSpan refreshInterval, string refreshSource)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(refreshSource);
        _time = time;
        RefreshInterval = refreshInterval;
        Threshold = refreshInterval * 2;
        _header = "UI stall threshold " + Ms(Threshold) + " (2 x refresh interval " + Ms(refreshInterval) + ", from " + refreshSource + ")";
    }

    public TimeSpan RefreshInterval { get; }

    // A queue wait or a run longer than this is recorded.
    public TimeSpan Threshold { get; }

    // For tests: how many entries the ring holds.
    internal int Count => _ring.Snapshot().Count;

    // Called where the action is posted: remembers when, and runs the action on the UI thread timing it.
    public Action Wrap(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        long posted = _time.GetTimestamp();
        return () =>
        {
            long started = _time.GetTimestamp();
            TimeSpan waited = _time.GetElapsedTime(posted, started);
            try
            {
                action();
            }
            finally
            {
                TimeSpan ran = _time.GetElapsedTime(started);
                if (waited > Threshold)
                {
                    _ring.Add(Stamp() + " UI post waited " + Ms(waited) + " in the queue: " + NameOf(action));
                }

                if (ran > Threshold)
                {
                    _ring.Add(Stamp() + " UI post ran " + Ms(ran) + ": " + NameOf(action));
                }
            }
        };
    }

    // The card's recorder: every window call of a card, in the same ring.
    public void RecordCardCall(CardWindowCall call) => _ring.Add(Stamp() + " card " + call);

    public IReadOnlyList<string> Lines()
    {
        IReadOnlyList<string> entries = _ring.Snapshot();
        var all = new List<string>(entries.Count + 1) { _header };
        all.AddRange(entries);
        return all;
    }

    private string Stamp() => _time.GetUtcNow().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static string Ms(TimeSpan span) => span.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) + " ms";

    // The method name of the action: its declaring type outside any compiler-made closure class, then the method.
    internal static string NameOf(Action action)
    {
        System.Reflection.MethodInfo method = action.Method;
        Type? type = method.DeclaringType;
        while (type is not null && type.Name.StartsWith('<'))
        {
            type = type.DeclaringType;
        }

        return (type?.Name ?? "?") + "." + method.Name;
    }
}
