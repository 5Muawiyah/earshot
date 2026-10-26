using Earshot.Bluetooth;
using Earshot.Contracts;
using Earshot.Tracing;
using Earshot.Tray;

namespace Earshot.App;

// Record a Bluetooth trace, from the settings page, from the card that says the AirPods could not be reached, and from a Fix
// connection result that points at this PC's Bluetooth (TraceRequestSignal). Each starts the same way: the note about what a
// trace holds is shown first, and only its Record button goes on. The recorder (package: Trace) then asks for one administrator
// prompt, raising only the installed copy, records until Stop, copies the trace to %LOCALAPPDATA%\Earshot\traces and opens the
// folder; the tray shows that it is running with a Stop button, and says how it ended. Nothing is sent anywhere. Not offered in
// safe mode, and with no installed copy the row says to set up Earshot first. A trace changes no device, service or pairing.
internal sealed partial class TrayContext
{
    private ITraceRecorder? _traceRecorder;

    private CardPlace _tracePlace = CardPlace.NearTray;

    private void WireTrace(TrayStartOptions options)
    {
        _traceRecorder = options.TraceRecorder;
        if (_traceRecorder is null)
        {
            return;
        }

        _traceRecorder.StateChanged += OnTraceStateChanged;

        // A helper an earlier run left behind with a session open: said once, at start.
        string? notice = _traceRecorder.StillRunningNotice();
        if (notice is not null)
        {
            _log.Warn("Bluetooth trace: " + notice);
            ShowCard(TrayStatus.AppName, notice, CardPlace.NearTray);
        }

        // Run folders an earlier recording left (a helper that could not delete its copy, or stopped part way): said once, at start.
        string? leftover = _traceRecorder.LeftoverNotice();
        if (leftover is not null)
        {
            _log.Warn("Bluetooth trace: " + leftover);
            ShowCard(TrayStatus.AppName, leftover, CardPlace.NearTray);
        }
    }

    private void UnwireTrace()
    {
        if (_traceRecorder is { } recorder)
        {
            recorder.StateChanged -= OnTraceStateChanged;
            recorder.Dispose();
            _traceRecorder = null;
        }
    }

    // Raised on whichever thread changed the state, so the card is shown from the UI thread. The state turns to Recording when
    // the helper is asked for, before the administrator prompt is answered, so neither the card nor the log says it records.
    // The running card stays up until it is pressed, clicked or replaced, and the settings row's button reads Stop while the
    // trace records, so Stop is always within reach: without it the recording would run its full ten minutes.
    private void OnTraceStateChanged(object? sender, EventArgs e)
    {
        bool recording = _traceRecorder is { State: TraceState.Recording };
        CardPlace place = _tracePlace;
        _registry.UiPost(() =>
        {
            _widgetCardPresenter?.Refresh();
            if (!recording)
            {
                return;
            }

            _log.Info("Bluetooth trace: started; it records once the administrator prompt is allowed.");
            ShowCardWithButton(TrayStatus.AppName, TraceCopy.Running, TraceCopy.StopButton, () => _traceRecorder?.Stop(), place, staysUp: true);
        });
    }

    internal void RecordTraceFromSettings()
    {
        _log.Info("Bluetooth trace: asked for from the settings page.");
        StartTrace(CardPlace.NearTray);
    }

    // A Fix connection run's result window asked for a trace. The same note and button as the row.
    internal void RecordTraceFromFixResult()
    {
        _log.Info("Bluetooth trace: asked for from a Fix connection result.");

        // The result's button asks for a trace; it is not the row's switch, so it never stops one that is running.
        if (_traceRecorder is { State: TraceState.Recording })
        {
            ShowCard(TrayStatus.AppName, TraceCopy.AlreadyRecording, CardPlace.NearTray);
            return;
        }

        StartTrace(CardPlace.NearTray);
    }

    // Whether the card may offer the trace button: a recorder, not safe mode, and an installed copy to raise.
    private bool TraceOffered => _traceRecorder is { NeedsSetUp: false } && !_registry.SafeMode;

    // Asking while a recording runs stops it: the row and the button are the same switch. Otherwise the note is shown, and the
    // recording starts only from its button.
    private void StartTrace(CardPlace place)
    {
        if (_closing)
        {
            return;
        }

        if (_registry.SafeMode)
        {
            _log.Info("Bluetooth trace: refused in safe mode.");
            ShowCard(TrayStatus.AppName, TraceCopy.SafeMode, place);
            return;
        }

        if (_traceRecorder is not { } recorder)
        {
            _log.Warn("Bluetooth trace: this tray was started without a recorder.");
            ShowCard(TrayStatus.AppName, TraceTrayText.NotAvailable, place);
            return;
        }

        if (recorder.State == TraceState.Recording)
        {
            _log.Info("Bluetooth trace: stop asked for.");
            recorder.Stop();
            return;
        }

        if (recorder.NeedsSetUp)
        {
            _log.Info("Bluetooth trace: no copy of Earshot new enough to record a trace is installed in Program Files, so there is none to raise.");
            ShowCard(TrayStatus.AppName, TraceCopy.SetUpFirst, place);
            return;
        }

        // The note is long enough to need its time, and nothing is recorded until its button is pressed, so it stays up too.
        ShowCardWithButton(TrayStatus.AppName, TraceCopy.NoteFor(recorder.DestinationFolder), TraceCopy.NoteButton, () => BeginTrace(place), place, staysUp: true);
    }

    // The note's Record button. Everything is asked again, because the card may have stayed up while things changed.
    private void BeginTrace(CardPlace place)
    {
        if (_closing || _registry.SafeMode || _traceRecorder is not { State: TraceState.Idle, NeedsSetUp: false } recorder)
        {
            _log.Info("Bluetooth trace: the note's button was pressed when a recording could not start, so nothing was asked.");
            return;
        }

        _log.Info("Bluetooth trace: the note was read and Record pressed.");
        _tracePlace = place;

        // Not through Launch: a recording runs for as long as the person likes (ten minutes at most) and Exit must not wait for it.
        // Earshot closing ends the helper, which watches the tray process.
        _ = RecordTraceAsync(recorder, place);
    }

    private async Task RecordTraceAsync(ITraceRecorder recorder, CardPlace place)
    {
        try
        {
            TraceResult result = await recorder.RecordAsync(_lifetime.Token);
            _log.Write(result.Outcome is TraceOutcome.Saved or TraceOutcome.SavedInPlace ? LogLevel.Info : LogLevel.Warn,
                "Bluetooth trace ended: " + result.Outcome + ".");
            ShowCard(TrayStatus.AppName, result.Message, place);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            _log.Info("Bluetooth trace: cancelled because Earshot is closing.");
        }
        catch (Exception ex)
        {
            ReportUnexpected("record a bluetooth trace", ex, place);
        }
    }
}

internal static class TraceTrayText
{
    public const string NotAvailable = "A Bluetooth trace can't be recorded here.";
}
