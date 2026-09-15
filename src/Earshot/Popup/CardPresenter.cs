using System.Globalization;
using System.Runtime.InteropServices;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Tray;

namespace Earshot.Popup;

// The popup card behind registry.Cards. The only ICardPresenter that shows anything.
//
// Threading. Show and Hide may be called from any thread. Each only queues its work through
// registry.UiPost, so the card window, its timer and every read of the desktop run on the UI thread, in
// the order the calls were made. One ConnectCard is created there the first time a card is shown and is
// reused for every later card.
//
// Show. A newer card replaces the content of the one on screen, moves it if needed, and restarts the
// dismiss timer. The card goes away after DismissAfter, when it is clicked, or on Hide.
//
// Cards nobody clicked for. A NearTray card (a notice, a background result) is shown only while
// SHQueryUserNotificationState reports QUNS_ACCEPTS_NOTIFICATIONS or QUNS_APP; in a full-screen app, a
// presentation or quiet time it is logged and skipped. A NearCursor card follows the user's own click, the
// accepted exception, so it always shows.
// https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shqueryusernotificationstate
// https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ne-shellapi-query_user_notification_state
//
// Failures. A card that cannot be placed or shown is logged with its native code and dropped; nothing is
// thrown back into the message loop, where the tray would report it on another card.
//
// Dispose on the UI thread, after the tray stops posting.
internal sealed class CardPresenter : ICardPresenter, IDisposable
{
    // How long a card stays up. A UI timing choice, not a measurement.
    public static readonly TimeSpan DismissAfter = TimeSpan.FromSeconds(4);

    private readonly ILog _log;
    private readonly Action<Action> _uiPost;
    private readonly ICardEnvironment _environment;
    private readonly Func<ICardSurface> _createCard;
    private readonly Func<ICardTimer> _createTimer;
    private ICardSurface? _card;
    private ICardTimer? _timer;
    private bool _disposed;

    public CardPresenter(ILog log, Action<Action> uiPost)
        : this(log, uiPost, new SystemCardEnvironment(log), () => new ConnectCard(log), static () => new FormsCardTimer())
    {
    }

    internal CardPresenter(ILog log, Action<Action> uiPost, ICardEnvironment environment, Func<ICardSurface> createCard, Func<ICardTimer> createTimer)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(uiPost);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(createCard);
        ArgumentNullException.ThrowIfNull(createTimer);
        _log = log;
        _uiPost = uiPost;
        _environment = environment;
        _createCard = createCard;
        _createTimer = createTimer;
    }

    public void Show(CardContent content, CardAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(content);
        _uiPost(() => ShowOnUiThread(content, anchor));
    }

    public void Hide() => _uiPost(HideOnUiThread);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_timer is not null)
        {
            _timer.Elapsed -= OnTimerElapsed;
            _timer.Dispose();
            _timer = null;
        }

        if (_card is not null)
        {
            _card.Clicked -= OnCardClicked;
            _card.Dispose();
            _card = null;
        }
    }

    // True for the notification states in which a card nobody clicked for may show.
    internal static bool AcceptsUnrequestedCard(int state) =>
        state is Shell.QUNS_ACCEPTS_NOTIFICATIONS or Shell.QUNS_APP;

    internal static string NotificationStateName(int state) => state switch
    {
        Shell.QUNS_NOT_PRESENT => "QUNS_NOT_PRESENT",
        Shell.QUNS_BUSY => "QUNS_BUSY",
        Shell.QUNS_RUNNING_D3D_FULL_SCREEN => "QUNS_RUNNING_D3D_FULL_SCREEN",
        Shell.QUNS_PRESENTATION_MODE => "QUNS_PRESENTATION_MODE",
        Shell.QUNS_ACCEPTS_NOTIFICATIONS => "QUNS_ACCEPTS_NOTIFICATIONS",
        Shell.QUNS_QUIET_TIME => "QUNS_QUIET_TIME",
        Shell.QUNS_APP => "QUNS_APP",
        _ => "QUNS " + state.ToString(CultureInfo.InvariantCulture),
    };

    private void ShowOnUiThread(CardContent content, CardAnchor anchor)
    {
        string what = anchor + " card \"" + content.Title + ": " + content.Status + "\"";
        _log.Write(LogLevel.Debug, "Show " + what + ".");
        if (_disposed)
        {
            _log.Write(LogLevel.Debug, "Not shown, the card is closed: " + what + ".");
            return;
        }

        // Only a card that follows a click skips the check; any other anchor is treated as NearTray.
        if (anchor != CardAnchor.NearCursor && !NotificationsAccepted(what))
        {
            return;
        }

        try
        {
            ICardSurface card = EnsureCard();
            ICardTimer timer = EnsureTimer();
            PlacementScene scene = _environment.ReadScene();
            CardTarget target = CardPlacement.TargetFor(anchor, scene);
            int dpi = _environment.DpiFor(target.Display);
            Size size = card.Prepare(content, dpi, _environment.ReadPalette(), CardPlacement.AvailableWidth(target, dpi));
            Rectangle bounds = CardPlacement.Place(anchor, scene, target, size, dpi);

            StepOutcome shown = card.ShowAt(bounds);
            if (!shown.Ok)
            {
                timer.Stop();
                _log.Error("Not shown: " + what + ". " + TrayReport.DescribeStep(shown));
                ReportHide(card.HideCard());
                return;
            }

            timer.Restart(DismissAfter);
            _log.Write(LogLevel.Debug, string.Create(CultureInfo.InvariantCulture,
                $"Shown at {bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}, {dpi} DPI, taskbar {target.Edge}: {what}."));
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or ArgumentException)
        {
            _log.Error("Not shown: " + what + ".", ex);
        }
    }

    private bool NotificationsAccepted(string what)
    {
        NotificationStateReading reading = _environment.QueryNotificationState();
        if (reading.HResult < 0)
        {
            StepOutcome step = StepOutcomes.FromHResult("sh-query-user-notification-state", reading.HResult);
            _log.Warn("Not shown, the notification state could not be read: " + what + ". " + TrayReport.DescribeStep(step));
            return false;
        }

        if (AcceptsUnrequestedCard(reading.State))
        {
            return true;
        }

        _log.Info("Not shown, Windows is not taking notifications now (" + NotificationStateName(reading.State) + "): " + what + ".");
        return false;
    }

    private void HideOnUiThread()
    {
        _timer?.Stop();
        if (_card is { IsDisposed: false } card)
        {
            ReportHide(card.HideCard());
        }
    }

    private void ReportHide(StepOutcome? hidden)
    {
        if (hidden is { Ok: false })
        {
            _log.Warn("The card could not be hidden: " + TrayReport.DescribeStep(hidden));
        }
    }

    private ICardSurface EnsureCard()
    {
        if (_card is { IsDisposed: false } card)
        {
            return card;
        }

        if (_card is not null)
        {
            // Closed from outside, for example by Application.Exit. A new one replaces it.
            _card.Clicked -= OnCardClicked;
        }

        _card = _createCard();
        _card.Clicked += OnCardClicked;
        return _card;
    }

    private ICardTimer EnsureTimer()
    {
        if (_timer is null)
        {
            _timer = _createTimer();
            _timer.Elapsed += OnTimerElapsed;
        }

        return _timer;
    }

    private void OnTimerElapsed(object? sender, EventArgs e) => HideOnUiThread();

    private void OnCardClicked(object? sender, EventArgs e)
    {
        _log.Write(LogLevel.Debug, "Card dismissed by a click.");
        HideOnUiThread();
    }
}
