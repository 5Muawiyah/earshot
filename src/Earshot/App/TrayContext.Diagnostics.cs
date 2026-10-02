using System.Globalization;
using System.Runtime.InteropServices;
using Earshot.Contracts;
using Earshot.Diagnostics;
using Earshot.Infra;
using Earshot.Popup;
using Earshot.Tray;
using Earshot.Update;

namespace Earshot.App;

// The tray's "Copy diagnostics" item: builds the redacted text (DiagnosticsText) and puts it on the clipboard. It reads
// the log file and what the frame log source holds and touches no device, so it is available while a switch runs.
internal sealed partial class TrayContext
{
    public const string DiagnosticsCopiedMessage = "Diagnostics copied. Names, addresses and ids are removed.";
    public const string DiagnosticsNotCopiedMessage = "Couldn't copy diagnostics. See the log.";

    private IFrameLogSource _frameLog = NullFrameLogSource.Instance;
    private Func<string> _diagnosticsLogPath = () => Paths.Current.LogFile;
    private Action<string> _setClipboardText = static text => Clipboard.SetText(text);

    private void WireDiagnostics(TrayStartOptions options)
    {
        _frameLog = options.FrameLog ?? NullFrameLogSource.Instance;
        _diagnosticsLogPath = options.DiagnosticsLogPath ?? _diagnosticsLogPath;
        _setClipboardText = options.SetClipboardText ?? _setClipboardText;
        _menu.CopyDiagnosticsClicked += (_, _) => CopyDiagnostics(ClickPlace());
    }

    // The strings the redactor removes by name: the paired device's friendly name as the tray shows it and the name the
    // owner gave their other device. The user name is added by the redactor itself. A window title is never logged by
    // this program, so there is none to list.
    private List<string> DiagnosticsKnownNames()
    {
        EarshotSettings settings = _registry.Settings.Current;
        var names = new List<string>();
        if (TrayStatus.ActiveTarget(_snapshot, settings)?.DisplayName is { Length: > 0 } device)
        {
            names.Add(device);
        }

        if (!string.IsNullOrWhiteSpace(settings.Widget.OtherDeviceLabel))
        {
            names.Add(settings.Widget.OtherDeviceLabel);
        }

        return names;
    }

    // UI thread: the clipboard needs a single-threaded apartment. A clipboard that another program holds open fails with
    // an HRESULT, which is logged in full and said on a card; it is never dropped.
    // https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.clipboard.settext
    private void CopyDiagnostics(CardPlace place)
    {
        if (_closing || _closed)
        {
            return;
        }

        string text;
        try
        {
            IReadOnlyList<string> lines = DiagnosticsText.ReadLogLines(_diagnosticsLogPath(), out string? readFailure);
            if (readFailure is not null)
            {
                _log.Warn(readFailure);
            }

            text = DiagnosticsText.Build(
                ReleaseVersion.Running(typeof(TrayContext).Assembly)?.ToString() ?? "unknown",
                RuntimeInformation.OSDescription,
                lines,
                _frameLog,
                DiagnosticsKnownNames());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn("Copy diagnostics: the text could not be built (" + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + ").", ex);
            ShowCard(TrayStatus.AppName, DiagnosticsNotCopiedMessage, place);
            return;
        }

        try
        {
            _setClipboardText(text);
        }
        catch (Exception ex) when (ex is ExternalException or ThreadStateException)
        {
            _log.Warn("Copy diagnostics: the clipboard refused the text (" + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + ").", ex);
            ShowCard(TrayStatus.AppName, DiagnosticsNotCopiedMessage, place);
            return;
        }

        _log.Info("Copy diagnostics: " + text.Length.ToString(CultureInfo.InvariantCulture) + " characters copied.");
        ShowCard(TrayStatus.AppName, DiagnosticsCopiedMessage, place);
    }
}
