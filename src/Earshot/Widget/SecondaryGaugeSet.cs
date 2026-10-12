using Earshot.Contracts;

namespace Earshot.Widget;

// The gauges on the other displays' taskbars while Gauge display is All displays: one SecondaryGauge for each display that is
// connected, is not the main display and shows a taskbar of its own (a Shell_SecondaryTrayWnd on its monitor). The main display's
// gauge is the tray's ordinary one, so with All displays and one display, or with no secondary taskbar, nothing here exists and
// the gauge behaves as Main display does.
//
// Reconcile makes the set match what is there now, and is called on the UI thread: on every read of the main taskbar, when the
// displays change or Explorer creates its taskbar again, once a minute, and when the setting changes. A display or a taskbar that
// appears gets a gauge. One that is not seen keeps its gauge until it has not been seen on enough reconciles over long enough
// (GaugeStability.RemoveWindow, RemoveReads): one walk of the windows that misses a taskbar Explorer is laying out again says
// nothing, and a gauge removed on it would come back only at a later reconcile. A reconcile that finds the taskbar ends the wait.
// Any display once the setting is not All displays has its gauge stopped and its window disposed, there and then. UI thread only.
internal sealed class SecondaryGaugeSet : IDisposable
{
    private readonly SecondaryGaugeParts _parts;
    private readonly IDisplaySource _displays;
    private readonly Func<SecondaryTaskbarReading> _taskbars;
    private readonly Dictionary<string, SecondaryGauge> _gauges = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Missing> _missing = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ITimer> _signalTimers = [];
    private ITimer? _recheck;
    private bool _disposed;
    private bool _wanted;
    private bool _reconciling;
    private bool _reconcileAgain;
    private bool _wantedNext;
    private bool _ownersReleased;

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

    // Adds the gauges for displays that have a taskbar now, and removes those whose display or taskbar has not been seen for
    // long enough. wanted false (Gauge display is not All displays) removes every gauge.
    //
    // A wait on the UI thread (stopping a removed gauge's watcher) lets messages in, and WM_DISPLAYCHANGE among them asks for
    // another Reconcile while this one is part way through. That one is not run inside this one, which would add a gauge this
    // pass has not stored yet a second time and leave the first running with nothing to stop it: it is noted, and this pass
    // goes round again with the newest answer once it is done.
    public void Reconcile(bool wanted)
    {
        if (_disposed)
        {
            return;
        }

        _wanted = wanted;
        if (_reconciling)
        {
            _reconcileAgain = true;
            _wantedNext = wanted;
            return;
        }

        _reconciling = true;
        try
        {
            bool current = wanted;
            do
            {
                _reconcileAgain = false;
                ReconcileOnce(current);
                current = _wantedNext;
            }
            while (_reconcileAgain && !_disposed);
        }
        finally
        {
            _reconciling = false;
        }
    }

    private void ReconcileOnce(bool wanted)
    {
        if (!wanted)
        {
            StopTimers();
            RemoveWhere(static _ => true, "Gauge display is no longer All displays");
            return;
        }

        _recheck ??= _parts.Time.CreateTimer(
            _ => _parts.UiPost(OnRecheckDue), null, GaugeStability.SetRecheckInterval, GaugeStability.SetRecheckInterval);

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

        foreach (SecondaryGauge gauge in _gauges.Values.ToList())
        {
            if (wantedDisplays.ContainsKey(gauge.DisplayId))
            {
                NoteSeen(gauge);
                continue;
            }

            DisplayInfo? display = reading.Displays.FirstOrDefault(d => string.Equals(d.Id, gauge.DisplayId, StringComparison.OrdinalIgnoreCase));

            // A taskbar window whose rectangle could not be read is missing from the list for that reason alone, so while one
            // is, a gauge is added for what is seen and no taskbar is counted as not seen. A display that is not there is.
            if (display is not null && bars.Problem is not null)
            {
                continue;
            }

            TaskbarMissing? why = display is null ? null : SecondaryTaskbarPicker.Explain(bars.Taskbars, display);
            if (NoteNotSeen(gauge, why))
            {
                if (_missing.Remove(gauge.DisplayId, out Missing? done))
                {
                    done.Cancel();
                }

                RemoveWhere(g => ReferenceEquals(g, gauge), bars.Problem is null ? "its display or taskbar is gone" : "its display is gone");
            }
        }

        foreach (DisplayInfo display in wantedDisplays.Values)
        {
            if (!_gauges.ContainsKey(display.Id))
            {
                Add(display, reading.Displays);
            }
        }
    }

    // The display's taskbar was seen: whatever was waiting to remove its gauge ends.
    private void NoteSeen(SecondaryGauge gauge)
    {
        if (!_missing.Remove(gauge.DisplayId, out Missing? missing))
        {
            return;
        }

        missing.Cancel();
        _parts.Log.Write(LogLevel.Debug, GaugeEventLog.TaskbarBack(gauge.Name, missing.Gate.Age, missing.DisplayItself));
    }

    // The display or its taskbar was not seen. True once that has held and the gauge is to go. The first time arms two more
    // reconciles, so an absence that lasts is settled inside its window and not at whatever next asks.
    private bool NoteNotSeen(SecondaryGauge gauge, TaskbarMissing? why)
    {
        if (!_missing.TryGetValue(gauge.DisplayId, out Missing? missing))
        {
            missing = new Missing(new HeldVerdict<bool>(_parts.Time, GaugeStability.RemoveWindow, GaugeStability.RemoveReads), displayItself: why is null);
            _missing[gauge.DisplayId] = missing;
        }

        bool starting = !missing.Gate.Pending;
        bool held = missing.Gate.Observe(true);
        if (starting)
        {
            _parts.Log.Write(LogLevel.Debug, GaugeEventLog.TaskbarNotSeen(gauge.Name, why, GaugeStability.RemoveWindow));
            missing.Arm(this, GaugeStability.RemoveConfirmDelays);
        }

        return held;
    }

    private void OnRecheckDue()
    {
        if (!_disposed && _wanted)
        {
            Reconcile(wanted: true);
        }
    }

    // The displays changed (WM_DISPLAYCHANGE): look now, and again at 2 s and 5 s, since Explorer builds the new secondary taskbar
    // some time after the signal.
    // https://learn.microsoft.com/en-us/windows/win32/gdi/wm-displaychange
    public void OnDisplayChanged() => OnSignal();

    // Explorer created its taskbar again (the registered "TaskbarCreated" message): the same.
    // https://learn.microsoft.com/en-us/windows/win32/shell/taskbar#taskbar-creation-notification
    public void OnTaskbarCreated() => OnSignal();

    private void OnSignal()
    {
        if (_disposed || !_wanted)
        {
            return;
        }

        Reconcile(wanted: true);
        foreach (ITimer timer in _signalTimers)
        {
            timer.Dispose();
        }

        _signalTimers.Clear();
        foreach (TimeSpan delay in GaugeStability.SignalFollowUpDelays)
        {
            _signalTimers.Add(ArmSignalFollowUp(delay));
        }
    }

    // A reconcile some time from now, on the UI thread. One that arrives after the set was told it is no longer wanted does nothing.
    private ITimer ArmSignalFollowUp(TimeSpan delay)
    {
        ITimer? timer = null;
        timer = _parts.Time.CreateTimer(
            _ => _parts.UiPost(() =>
            {
                if (timer is not null)
                {
                    _ = _signalTimers.Remove(timer);
                    timer.Dispose();
                }

                if (!_disposed && _wanted)
                {
                    Reconcile(wanted: true);
                }
            }),
            null, delay, Timeout.InfiniteTimeSpan);
        return timer;
    }

    private void StopTimers()
    {
        _recheck?.Dispose();
        _recheck = null;
        foreach (ITimer timer in _signalTimers)
        {
            timer.Dispose();
        }

        _signalTimers.Clear();
        foreach (Missing missing in _missing.Values)
        {
            missing.Cancel();
        }

        _missing.Clear();
    }

    // A display's gauge whose display or taskbar was not seen, how long that has held, and the reconciles armed to settle it.
    private sealed class Missing(HeldVerdict<bool> gate, bool displayItself)
    {
        private readonly List<ITimer> _timers = [];
        private int _epoch;

        public HeldVerdict<bool> Gate { get; } = gate;

        // The display was not there at all, and not only its taskbar.
        public bool DisplayItself { get; } = displayItself;

        public void Arm(SecondaryGaugeSet owner, TimeSpan[] delays)
        {
            int epoch = _epoch;
            foreach (TimeSpan delay in delays)
            {
                ITimer? timer = null;
                timer = owner._parts.Time.CreateTimer(
                    _ => owner._parts.UiPost(() =>
                    {
                        if (timer is not null)
                        {
                            _ = _timers.Remove(timer);
                            timer.Dispose();
                        }

                        if (!owner._disposed && owner._wanted && epoch == _epoch)
                        {
                            owner.Reconcile(wanted: true);
                        }
                    }),
                    null, delay, Timeout.InfiniteTimeSpan);
                _timers.Add(timer);
            }
        }

        public void Cancel()
        {
            _epoch++;
            foreach (ITimer timer in _timers)
            {
                timer.Dispose();
            }

            _timers.Clear();
        }
    }

    private void Add(DisplayInfo display, IReadOnlyList<DisplayInfo> all)
    {
        string name = DisplayNames.Short(display, all);
        var gauge = new SecondaryGauge(display, name, _parts, g => LaidOut?.Invoke(this, g));
        gauge.CardRequested += (_, _) => CardRequested?.Invoke(this, gauge);
        gauge.ToggleRequested += (_, _) => ToggleRequested?.Invoke(this, gauge);
        gauge.MenuRequested += (_, point) => MenuRequested?.Invoke(this, (gauge, point));
        if (_ownersReleased)
        {
            gauge.ReleaseOwner();
        }

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

    // Every gauge is taken off its taskbar's ownership, and a gauge added while this holds is not owned either, until
    // ResumeOwners: the wait the caller is about to make must not find one still tied to a window of the shell.
    public void ReleaseOwners()
    {
        _ownersReleased = true;
        foreach (SecondaryGauge gauge in _gauges.Values.ToList())
        {
            gauge.ReleaseOwner();
        }
    }

    public void ResumeOwners()
    {
        _ownersReleased = false;
        foreach (SecondaryGauge gauge in _gauges.Values.ToList())
        {
            gauge.ResumeOwner();
        }
    }

    public void NotifyChanged()
    {
        foreach (SecondaryGauge gauge in _gauges.Values.ToList())
        {
            gauge.NotifyChanged();
        }
    }

    public void SetBaselinePollInterval(int milliseconds)
    {
        foreach (SecondaryGauge gauge in _gauges.Values.ToList())
        {
            gauge.SetBaselinePollInterval(milliseconds);
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

        StopTimers();
        RemoveWhere(static _ => true, "the gauge is closing");
        _disposed = true;
    }
}
