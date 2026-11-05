using Earshot.Tray;

namespace Earshot.Widget;

// The full card's Connect and Disconnect button while a connect or a disconnect runs: the label fades out and a 16 px ring in the
// accent turns once every 1000 ms in its place; the button keeps its control fill and ignores a press. When the work ends the ring
// fades back to the label. With animation effects off nothing moves: the ring is a still 90 degree arc at 12 o'clock and the
// change lands in one step.
internal sealed partial class WidgetCard
{
    private CardButtonWork _workShown = CardButtonWork.None;
    private readonly AnimatedValue _workFade = new(1);
    private double _workFadeP = 1;
    private readonly object _workSpinKey = new();
    private TimeSpan _workSpinStart;
    private float _workAngle;

    // What the button shows now, how far its fade has got (1 once it has settled) and the ring's angle, for tests.
    internal CardButtonWork WorkShown => _workShown;

    internal double WorkFadeProgress => _workFadeP;

    internal float WorkRingAngleForTest => _workAngle;

    // The button is running a connect or a disconnect: its ring shows, and a press does nothing.
    private bool Working => _workShown != CardButtonWork.None;

    private void NoteWork(CardButtonWork work)
    {
        if (work == _workShown)
        {
            return;
        }

        bool wasRing = Working;
        _workShown = work;
        if (wasRing == Working)
        {
            return;
        }

        _workFade.Jump(0);
        _workFadeP = 0;
        _workFade.AnimateTo(1, CanAnimateContent ? _driver : null, CompactMotion.ActionFadeDuration, FluentMotion.Linear, p =>
        {
            _workFadeP = p;
            MarkDirty(_mainLayout.Button);
        });

        if (Working)
        {
            StartWorkSpin();
        }
        else
        {
            StopWorkSpin();
        }
    }

    private void StartWorkSpin()
    {
        _workAngle = 0;
        if (!CanAnimateContent || _driver is null)
        {
            return;
        }

        _workSpinStart = _driver.Now;
        _driver.Run(_workSpinKey, at =>
        {
            double turn = (at - _workSpinStart).TotalMilliseconds % CompactMotion.TurnDuration.TotalMilliseconds / CompactMotion.TurnDuration.TotalMilliseconds;
            _workAngle = (float)(turn * 360.0);
            MarkDirty(_mainLayout.Button);
            return Working;
        });
    }

    private void StopWorkSpin()
    {
        _driver?.Stop(_workSpinKey);
        _workAngle = 0;
    }

    // The button's face: the label, the ring, or the two crossing while the work starts or ends.
    private void DrawButtonFace(Graphics g, Rectangle rect, string label, Color text, CardColours colours)
    {
        Color ring = colours.HighContrast ? colours.Text : colours.Accent;
        if (_workFadeP >= 1)
        {
            if (Working)
            {
                DrawRing(g, rect, ring, _workAngle);
            }
            else
            {
                DrawText(g, rect, label, text, TypeRole.Body, StringAlignment.Center);
            }

            return;
        }

        double ringOpacity = Working ? _workFadeP : 1 - _workFadeP;
        DrawText(g, rect, label, WithOpacity(text, 1 - ringOpacity), TypeRole.Body, StringAlignment.Center);
        DrawRing(g, rect, WithOpacity(ring, ringOpacity), _workAngle);
    }

    // What a screen reader calls the button: its words, or the work while it runs.
    private string ButtonName => _model.Work switch
    {
        CardButtonWork.Connecting => WidgetCopy.GaugeConnecting,
        CardButtonWork.Disconnecting => WidgetCopy.GaugeDisconnecting,
        _ => ButtonLabel,
    };
}
