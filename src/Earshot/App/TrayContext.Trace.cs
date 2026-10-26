using Earshot.Bluetooth;
using Earshot.Contracts;
using Earshot.Tracing;
using Earshot.Tray;

namespace Earshot.App;

// Record a Bluetooth trace, from the settings page and from the card that says the AirPods could not be reached. The recorder
// (package: Trace) asks for one administrator prompt, records until Stop, copies the trace to Documents and opens the folder; the
// tray only asks for it, shows that it is running with a Stop button, and says how it ended. Nothing is sent anywhere. Not
// offered in safe mode. A trace changes no device, service or pairing.
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

    // Raised on whichever thread changed the state, so the card is shown from the UI thread.
    private void OnTraceStateChanged(object? sender, EventArgs e)
    {
        if (_traceRecorder is not { State: TraceState.Recording })
        {
            return;
        }

        CardPlace place = _tracePlace;
        _registry.UiPost(() =>
        {
            _log.Info("Bluetooth trace: recording.");
            ShowCardWithButton(TrayStatus.AppName, TraceCopy.Running, TraceCopy.StopButton, () => _traceRecorder?.Stop(), place);
        });
    }

    internal void RecordTraceFromSettings()
    {
        _log.Info("Bluetooth trace: asked for from the settings page.");
        StartTrace(CardPlace.NearTray);
    }

    // Asking while a recording runs stops it: the row and the button are the same switch.
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
