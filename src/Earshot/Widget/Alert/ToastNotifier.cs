using System.Globalization;
using Earshot.Contracts;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Earshot.Widget.Alert;

// Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier(applicationId), guarded the way
// WindowsStreamingPlatform guards every one of its WinRT calls: an early return on WidgetPlatformGuard,
// then the call, in the same method. No test here ever shows a toast: the first real Show is a live test
// the owner runs.
// https://learn.microsoft.com/en-us/uwp/api/windows.ui.notifications.toastnotificationmanager.createtoastnotifier
internal sealed class ToastNotifier : INotifier
{
    private readonly string _appUserModelId;
    private readonly INotifier _fallback;
    private readonly ILog _log;
    private readonly Func<string, string, StepOutcome>? _showOverride;
    private long _fallenBackToCard;

    public ToastNotifier(string appUserModelId, INotifier fallback, ILog log)
        : this(appUserModelId, fallback, log, null)
    {
    }

    // showOverride lets a test replace the guarded WinRT call with a fake outcome, so the fallback and
    // logging behaviour can be proved without depending on whether this machine has a registered
    // AppUserModelID to show a toast against.
    internal ToastNotifier(string appUserModelId, INotifier fallback, ILog log, Func<string, string, StepOutcome>? showOverride)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appUserModelId);
        ArgumentNullException.ThrowIfNull(fallback);
        ArgumentNullException.ThrowIfNull(log);
        _appUserModelId = appUserModelId;
        _fallback = fallback;
        _log = log;
        _showOverride = showOverride;
    }

    // How many times NotifyAsync has fallen back to the card because the toast could not be shown.
    internal long AlertsFallenBackToCard => Interlocked.Read(ref _fallenBackToCard);

    public async Task<StepOutcome> NotifyAsync(string title, string text)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(text);

        StepOutcome step = _showOverride is not null ? _showOverride(title, text) : Show(title, text);
        if (step.Ok)
        {
            return step;
        }

        Interlocked.Increment(ref _fallenBackToCard);
        await _fallback.NotifyAsync(title, text).ConfigureAwait(false);
        return step;
    }

    private StepOutcome Show(string title, string text)
    {
        if (!WidgetPlatformGuard.HasToastNotifications)
        {
            return StepOutcomes.NotAvailable("toast-show", "This build of Windows has no toast notifier.");
        }

        try
        {
            XmlDocument xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
            XmlNodeList textNodes = xml.GetElementsByTagName("text");
            textNodes[0].AppendChild(xml.CreateTextNode(title));
            textNodes[1].AppendChild(xml.CreateTextNode(text));

            var toast = new ToastNotification(xml);
            ToastNotificationManager.CreateToastNotifier(_appUserModelId).Show(toast);
            return StepOutcomes.FromHResult("toast-show", 0);
        }
        catch (Exception ex)
        {
            // Not a silent catch: the HRESULT and the exception type are logged here, and the caller falls
            // back to the card. Caught by its base type on purpose, because the WinRT projection can
            // surface more than one exception type for a notifier with no registered AppUserModelID, and
            // every one of them must still fall back rather than stop the widget.
            _log.Warn(DescribeFailure(ex));
            return StepOutcomes.FromHResult("toast-show", ex.HResult, detail: ex.GetType().Name, ok: false);
        }
    }

    // The log line's own shape, pulled out so it can be proved directly: no test may trigger the real
    // WinRT call failing, since that would depend on this machine's own notifier state.
    internal static string DescribeFailure(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return "Could not show a toast (0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + " " + ex.GetType().Name + "): " + ex.Message;
    }
}
