using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Earshot.Widget;

// How the card's pixels reach the window. The card draws over a translucent backdrop, so its content starts from a clear to
// alpha 0 and is drawn on top of that. Done on the window's own surface the window shows the backdrop alone from the clear until
// the last string is drawn, which is a frame of an empty card whenever the compositor takes the window in between: a flash,
// on every repaint of every update. So the card is drawn into a bitmap of its own (premultiplied ARGB, the format the compositor
// uses) and the finished pixels are copied to the window in one operation with the source copied as it is, alpha included. The
// copy is byte for byte what drawing on the window made (WidgetCardBufferedPaintTests).
//
// A repaint that was asked for because something may have changed (a status update, a refresh, a progress tick) is first drawn
// into the bitmap and compared with what the window shows; only the pixels that differ are invalidated, and none are when none
// differ, which is what almost every status update is. What the window shows is remembered as a copy of the last pixels copied to
// it, and forgotten whenever the window might not hold them (hidden, resized, its handle made again).
internal sealed partial class WidgetCard
{
    private Bitmap? _frame;
    private Bitmap? _shownFrame;
    private bool _shownFrameValid;
    private int _frames;
    private int _blits;
    private int _skippedUnchanged;

    // How many frames were drawn into the bitmap, how many copies went to the window, and how many requests to repaint found
    // nothing different and invalidated nothing. For tests, and for the debug line the presenter writes.
    internal PaintStatistics PaintCounts => new(_frames, _blits, _skippedUnchanged);

    // The card's content in the bitmap, drawn afresh. The bitmap is kept for the next frame while the size stays.
    private Bitmap DrawFrame()
    {
        Size size = ClientSize;
        if (_frame is null || _frame.Size != size)
        {
            _frame?.Dispose();
            _frame = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppPArgb);
        }

        using Graphics g = Graphics.FromImage(_frame);
        RenderContent(g);
        _frames++;
        return _frame;
    }

    // Paints clip of the window onto target: the frame drawn now, copied with SourceCopy so the alpha the clear and the drawing
    // made is what lands in the window, not blended with what was there. WM_PAINT arrives here through OnPaint.
    internal void PaintBuffered(Graphics target, Rectangle clip)
    {
        ArgumentNullException.ThrowIfNull(target);
        Rectangle area = Rectangle.Intersect(clip, ClientRectangle);
        if (area.IsEmpty)
        {
            return;
        }

        Bitmap frame = DrawFrame();
        target.CompositingMode = CompositingMode.SourceCopy;
        target.DrawImage(frame, area, area.X, area.Y, area.Width, area.Height, GraphicsUnit.Pixel);
        _blits++;
        RememberShown(frame, area);
    }

    // The window now shows frame inside area. Only a copy of the whole client makes the memory valid; after that each smaller
    // copy updates the part it covers.
    private void RememberShown(Bitmap frame, Rectangle area)
    {
        bool whole = area == ClientRectangle;
        if (_shownFrame is null || _shownFrame.Size != frame.Size)
        {
            _shownFrame?.Dispose();
            _shownFrame = null;
            _shownFrameValid = false;
            if (!whole)
            {
                return;
            }

            _shownFrame = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppPArgb);
        }
        else if (!_shownFrameValid && !whole)
        {
            return;
        }

        using Graphics g = Graphics.FromImage(_shownFrame!);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(frame, area, area.X, area.Y, area.Width, area.Height, GraphicsUnit.Pixel);
        _shownFrameValid = true;
    }

    // The size is set only when it is not the size already: a call that changes nothing still goes to the window manager.
    private void SetClientSizeIfChanged(Size size)
    {
        if (ClientSize != size)
        {
            Record(CardWindowCallKind.ClientSize, new Rectangle(Point.Empty, size), "client size");
            ClientSize = size;
        }
    }

    // The window may not hold what the card last copied to it: the next request to repaint asks for the whole card.
    private void ForgetShownFrame() => _shownFrameValid = false;

    // Asks for a repaint of what has changed since the window was last painted, and of nothing when nothing has: the new frame is
    // drawn and compared with what the window shows, and only the box round the pixels that differ is invalidated. A card that
    // is not on screen, or has not been painted whole yet, is invalidated whole.
    private void RepaintIfChanged()
    {
        if (!IsHandleCreated || !Visible || !_shownFrameValid || _shownFrame is null || _shownFrame.Size != ClientSize)
        {
            Invalidate();
            return;
        }

        Rectangle? changed = Difference(_shownFrame, DrawFrame());
        if (changed is null)
        {
            _skippedUnchanged++;
            return;
        }

        Invalidate(changed.Value);
    }

    // The smallest box that holds every pixel that differs between two frames of the same size, or null when none does.
    private static Rectangle? Difference(Bitmap before, Bitmap after)
    {
        int width = before.Width;
        int height = before.Height;
        var area = new Rectangle(0, 0, width, height);
        BitmapData a = before.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        BitmapData b = after.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            int stride = width * 4;
            var rowA = new byte[stride];
            var rowB = new byte[stride];
            int top = int.MaxValue;
            int bottom = -1;
            int left = int.MaxValue;
            int right = -1;
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(a.Scan0 + (y * a.Stride), rowA, 0, stride);
                Marshal.Copy(b.Scan0 + (y * b.Stride), rowB, 0, stride);
                if (rowA.AsSpan().SequenceEqual(rowB))
                {
                    continue;
                }

                top = Math.Min(top, y);
                bottom = y;
                for (int x = 0; x < width; x++)
                {
                    int o = x * 4;
                    if (rowA[o] != rowB[o] || rowA[o + 1] != rowB[o + 1] || rowA[o + 2] != rowB[o + 2] || rowA[o + 3] != rowB[o + 3])
                    {
                        left = Math.Min(left, x);
                        right = Math.Max(right, x);
                    }
                }
            }

            return bottom < 0 ? null : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
        }
        finally
        {
            before.UnlockBits(a);
            after.UnlockBits(b);
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        ForgetShownFrame();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        ForgetShownFrame();
        base.OnHandleDestroyed(e);
    }

    private void DisposeFrames()
    {
        _frame?.Dispose();
        _frame = null;
        _shownFrame?.Dispose();
        _shownFrame = null;
        _shownFrameValid = false;
    }
}

// What the card's painting has done, for tests: frames drawn into the bitmap, copies made to the window, and requests to repaint
// that found no pixel different.
internal readonly record struct PaintStatistics(int Frames, int Blits, int SkippedUnchanged);
