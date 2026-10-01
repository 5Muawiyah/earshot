using Earshot.Contracts;

namespace Earshot.Widget;

// The gauges on the other displays' taskbars while Gauge display is All displays: one SecondaryGauge for each display that is
// connected, is not the main display and shows a taskbar of its own (a Shell_SecondaryTrayWnd on its monitor). The main display's
// gauge is the tray's ordinary one, so with All displays and one display, or with no secondary taskbar, nothing here exists and
// the gauge behaves as Main display does.
//
// Reconcile makes the set match what is there now, and is called on the UI thread: on every read of the main taskbar (about once a
// second), when the displays change, and when the setting changes. A display or a taskbar that appears gets a gauge; one that goes,
// or any display once the setting is not All displays, has its gauge stopped and its window disposed, there and then. UI thread only.
internal sealed class SecondaryGaugeSet : IDisposable
{
    private readonly SecondaryGaugeParts _parts;
    private readonly IDisplaySource _displays;
    private readonly Func<SecondaryTaskbarReading> _taskbars;
    private readonly Dictionary<string, SecondaryGauge> _gauges = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public SecondaryGaugeSet(SecondaryGaugeParts parts, IDisplaySource displays, Func<SecondaryTaskbarReading> taskbars)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(displays);
        ArgumentNullException.ThrowIfNull(taskbars);
        _parts = parts;
        _displays = displays;
        _taskbars = taskbars;
    }

    // A gauge asked for the card, to connect, or for the menu; or has just taken a read and may need drawing.
    public event EventHandler<SecondaryGauge>? CardRequested;

    public event EventHandler<SecondaryGauge>? ToggleRequested;

    public event EventHandler<(SecondaryGauge Gauge, Point Point)>? MenuRequested;

    public event EventHandler<SecondaryGauge>? LaidOut;

    public IReadOnlyCollection<SecondaryGauge> Gauges => _gauges.Values;

    public int Count => _gauges.Count;

    // Adds the gauges for displays that have a taskbar now and removes the rest. wanted false (Gauge display is not All displays)
    // removes every gauge.
    public void Reconcile(bool wanted)
    {
        if (_disposed)
        {
            return;
        }

        if (!wanted)
        {
            RemoveWhere(static _ => true, "Gauge display is no longer All displays");
            return;
        }

        DisplayReading reading = _displays.Read();
        if (reading.Displays.Count == 0)
        {
            // Windows could not be asked: that says nothing about which displays are there, so no gauge is removed on it.
            return;
        }

        SecondaryTaskbarReading bars = _taskbars();
        var wantedDisplays = new Dictionary<string, DisplayInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (DisplayInfo display in reading.Displays)
        {
            if (!display.IsPrimary && SecondaryTaskbarPicker.Pick(bars.Taskbars, display) is not null)
            {
                wantedDisplays[display.Id] = display;
            }
        }

        // A taskbar window whose rectangle could not be read is missing from the list for that reason alone, so while one
        // is, a gauge is added for what is seen and none is removed for what is not.
        if (bars.Problem is null)
        {
            RemoveWhere(g => !wantedDisplays.ContainsKey(g.DisplayId), "its display or taskbar is gone");
        }
        else
        {
            RemoveWhere(g => reading.Displays.All(d => !string.Equals(d.Id, g.DisplayId, StringComparison.OrdinalIgnoreCase)), "its display is gone");
        }

        foreach (DisplayInfo display in wantedDisplays.Values)
        {
            if (!_gauges.ContainsKey(display.Id))
            {
                Add(display, reading.Displays);
            }
        }
    }

    private void Add(DisplayInfo display, IReadOnlyList<DisplayInfo> all)
    {
        string name = DisplayNames.Short(display, all);
        var gauge = new SecondaryGauge(display, name, _parts, g => LaidOut?.Invoke(this, g));
        gauge.CardRequested += (_, _) => CardRequested?.Invoke(this, gauge);
        gauge.ToggleRequested += (_, _) => ToggleRequested?.Invoke(this, gauge);
        gauge.MenuRequested += (_, point) => MenuRequested?.Invoke(this, (gauge, point));
        _gauges[display.Id] = gauge;
        _parts.Log.Info("Gauge: a gauge is added for " + name + " (All displays).");
    }

    private void RemoveWhere(Func<SecondaryGauge, bool> match, string why)
    {
        foreach (SecondaryGauge gauge in _gauges.Values.Where(match).ToList())
        {
            _ = _gauges.Remove(gauge.DisplayId);
            gauge.Dispose();
            _parts.Log.Info("Gauge: the gauge for " + gauge.Name + " is removed (" + why + ").");
        }
    }

    public void OnForegroundChanged(string foregroundClass)
    {
        foreach (SecondaryGauge gauge in _gauges.Values.ToList())
        {
            gauge.OnForegroundChanged(foregroundClass);
        }
    }

    public void OnShellWindowChanged(string windowClass, bool shown)
    {
        foreach (SecondaryGauge gauge in _gauges.Values.ToList())
        {
            gauge.OnShellWindowChanged(windowClass, shown);
        }
    }

    public void NotifyFullScreenApp(bool opening)
    {
        foreach (SecondaryGauge gauge in _gauges.Values.ToList())
        {
            gauge.NotifyFullScreenApp(opening);
        }
    }

    public void Poke(bool resetBackoff)
    {
        foreach (SecondaryGauge gauge in _gauges.Values.ToList())
        {
            if (resetBackoff)
            {
                gauge.ResetBackoff();
            }

            gauge.Poke();
        }
    }

    public void Render(WidgetSnapshot snapshot, DateTimeOffset now, GaugeDisplaySettings settings, Color ink, string fontFamily)
    {
        foreach (SecondaryGauge gauge in _gauges.Values.ToList())
        {
            gauge.Render(snapshot, now, settings, ink, fontFamily);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        RemoveWhere(static _ => true, "the gauge is closing");
        _disposed = true;
    }
}
