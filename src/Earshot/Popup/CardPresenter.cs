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
// Where a card after a click goes. The notification area guidance asks for a popup raised by a click to
// sit near the click, but a result card often comes long after the click (a connect waits for the
// device), when the cursor may be anywhere. So a NearCursor card is anchored at the first of:
//   1. the click point the caller captured when the click happened, when it is on a display;
//   2. the point the last NearCursor card was anchored at, when a card was shown there within
//      ClickAnchorLifetime and it is still on a display, so a result card lands where the card for the
//      same click did and a replaced card does not jump;
//   3. the cursor, when it is on the taskbar, where a click on the icon happens;
// and otherwise it goes to the corner near the notification area, like a NearTray card but without the
// notification state check.
// https://learn.microsoft.com/en-us/windows/win32/shell/notification-area
//
// Cards nobody clicked for. A NearTray card (a notice, a background result) is shown only while
// SHQueryUserNotificationState reports QUNS_ACCEPTS_NOTIFICATIONS or QUNS_APP; in a full-screen app, a
// presentation or quiet time it is logged and skipped. A NearCursor card follows the user's own click, the
// accepted exception, so it always shows.
// https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shqueryusernotificationstate
// https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ne-shellapi-query_user_notification_state
//
// Failures. A card that cannot be placed, drawn or shown is logged with its native code and dropped; nothing
// is thrown back into the message loop, where the tray would report it on another card.
//
// Dispose on the UI thread, after the tray stops posting.
internal sealed class CardPresenter : ICardPresenter, IDisposable
{
    // How long a card stays up. A UI timing choice, not a measurement.
    public static readonly TimeSpan DismissAfter = TimeSpan.FromSeconds(4);

    // How long after a card was last shown at a click point that point still anchors the next card after
    // a click. A UI timing choice, not a measurement: meant to outlast the wait of a connect or disconnect,
    // including one that allows the device first, while an older click's point is not reused. A result
    // that comes later still goes to the cursor on the taskbar or the corner near the notification area.
    public static readonly TimeSpan ClickAnchorLifetime = TimeSpan.FromSeconds(60);

    private readonly ILog _log;
    private readonly Action<Action> _uiPost;
    private readonly ICardEnvironment _environment;
    private readonly Func<ICardSurface> _createCard;
    private readonly Func<ICardTimer> _createTimer;
    private readonly TimeProvider _time;
    private ICardSurface? _card;
    private ICardTimer? _timer;
    private Point? _clickAnchor;
    private long _clickAnchorShownAt;
    private bool _disposed;

    public CardPresenter(ILog log, Action<Action> uiPost)
        : this(log, uiPost, new SystemCardEnvironment(log), () => new ConnectCard(log), static () => new FormsCardTimer(), TimeProvider.System)
    {
    }

    internal CardPresenter(ILog log, Action<Action> uiPost, ICardEnvironment environment, Func<ICardSurface> createCard, Func<ICardTimer> createTimer, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(uiPost);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(createCard);
        ArgumentNullException.ThrowIfNull(createTimer);
        ArgumentNullException.ThrowIfNull(time);
        _log = log;
        _uiPost = uiPost;
        _environment = environment;
        _createCard = createCard;
        _createTimer = createTimer;
        _time = time;
    }

    public void Show(CardContent content, CardAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(content);
        _uiPost(() => ShowOnUiThread(content, anchor, clickPoint: null));
    }

    // Show for a card that follows a click at clickPoint: the cursor position in physical pixels, read
    // when the click happened rather than when the card is shown. It anchors a NearCursor card; a NearTray
    // card ignores it.
    public void Show(CardContent content, CardAnchor anchor, Point clickPoint)
    {
        ArgumentNullException.ThrowIfNull(content);
        _uiPost(() => ShowOnUiThread(content, anchor, clickPoint));
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

    private void ShowOnUiThread(CardContent content, CardAnchor anchor, Point? clickPoint)
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

            // Any anchor other than NearCursor is placed as NearTray.
            CardAnchor placement = CardAnchor.NearTray;
            Point? anchoredAt = null;
            string where = "near the notification area";
            if (anchor == CardAnchor.NearCursor)
            {
                (anchoredAt, where) = ClickAnchorFor(scene, clickPoint);
                if (anchoredAt is { } point)
                {
                    placement = CardAnchor.NearCursor;
                    scene = scene with { Cursor = point };
                }
            }

            CardTarget target = CardPlacement.TargetFor(placement, scene);
            int dpi = _environment.DpiFor(target.Display);
            Size size = card.Prepare(content, dpi, _environment.ReadPalette(), CardPlacement.AvailableWidth(target, dpi));
            Rectangle bounds = CardPlacement.Place(placement, scene, target, size, dpi);

            StepOutcome shown = card.ShowAt(bounds);
            if (!shown.Ok)
            {
                timer.Stop();
                _log.Error("Not shown: " + what + ". " + TrayReport.DescribeStep(shown));
                ReportHide(card.HideCard());
                return;
            }

            if (anchoredAt is { } used)
            {
                _clickAnchor = used;
                _clickAnchorShownAt = _time.GetTimestamp();
            }

            timer.Restart(DismissAfter);
            _log.Write(LogLevel.Debug, string.Create(CultureInfo.InvariantCulture,
                $"Shown at {bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}, {dpi} DPI, taskbar {target.Edge}, {where}: {what}."));
        }
        catch (Exception ex)
        {
            // Every failure while a card is measured, drawn or shown is logged here with what it was for, and
            // the card is dropped. The net is this wide on purpose: System.Drawing turns several GDI+ statuses
            // into exceptions that are not ExternalException (OutOfMemoryException for the GDI+ OutOfMemory
            // status, say), and anything that reached the message loop would be answered by the tray with
            // another card, through this same path.
            // https://learn.microsoft.com/en-us/dotnet/api/system.drawing.image.fromfile
            _log.Error("Not shown: " + what + ".", ex);
        }
    }

    // The point a NearCursor card is anchored at, in the order the class comment gives, and how it was
    // chosen for the log. Null sends the card to the corner near the notification area.
    private (Point? Point, string Where) ClickAnchorFor(PlacementScene scene, Point? clickPoint)
    {
        if (clickPoint is { } click && CardPlacement.IsOnADisplay(click, scene.Displays))
        {
            return (click, Describe("anchored at the click", click));
        }

        if (_clickAnchor is { } last &&
            _time.GetElapsedTime(_clickAnchorShownAt) <= ClickAnchorLifetime &&
            CardPlacement.IsOnADisplay(last, scene.Displays))
        {
            return (last, Describe("anchored where the last card after a click was", last));
        }

        if (CardPlacement.IsOnTaskbar(scene, scene.Cursor))
        {
            return (scene.Cursor, Describe("anchored at the cursor on the taskbar", scene.Cursor));
        }

        return (null, Describe("near the notification area, because the cursor is away from the taskbar", scene.Cursor));
    }

    private static string Describe(string where, Point point) =>
        string.Create(CultureInfo.InvariantCulture, $"{where} ({point.X},{point.Y})");

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
