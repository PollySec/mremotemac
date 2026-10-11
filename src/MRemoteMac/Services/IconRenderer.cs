using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace MRemoteMac.Services;

/// <summary>
/// Draws the application icon procedurally (macOS squircle, deep blue gradient, display with a signal arc and a cursor)
/// and saves it as PNG. Used by packaging/make-app.sh to produce the .icns and by the window/about icon.
/// </summary>
public static class IconRenderer
{
    public static void Render(string path, int size = 1024)
    {
        using var rtb = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var ctx = rtb.CreateDrawingContext())
            Draw(ctx, size);
        rtb.Save(path);
    }

    public static void Draw(DrawingContext ctx, double s)
    {
        // macOS icon grid: the squircle occupies ~82% of the canvas
        var inset = s * 0.09;
        var rect = new Rect(inset, inset, s - 2 * inset, s - 2 * inset);
        var radius = rect.Width * 0.225;

        // soft drop shadow
        for (var i = 6; i >= 1; i--)
        {
            var sh = rect.Inflate(i * s * 0.004).Translate(new Vector(0, i * s * 0.004));
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(10 + i * 2), 0, 0, 0)), null, sh, radius, radius);
        }

        // background: deep blue to teal gradient
        var bg = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse("#1F3B8F"), 0),
                new GradientStop(Color.Parse("#2563EB"), 0.55),
                new GradientStop(Color.Parse("#06B6D4"), 1)
            }
        };
        ctx.DrawRectangle(bg, null, rect, radius, radius);

        // subtle top sheen
        var sheen = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5, 0.6, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(70, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1) }
        };
        ctx.DrawRectangle(sheen, null, rect, radius, radius);

        // monitor: rounded screen with bezel
        var mw = rect.Width * 0.62;
        var mh = mw * 0.64;
        var mx = rect.X + (rect.Width - mw) / 2;
        var my = rect.Y + rect.Height * 0.21;
        var screen = new Rect(mx, my, mw, mh);
        var screenR = mw * 0.08;
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), null, screen.Translate(new Vector(0, s * 0.012)).Inflate(s * 0.006), screenR, screenR);
        ctx.DrawRectangle(Brushes.White, null, screen, screenR, screenR);
        var inner = screen.Deflate(mw * 0.045);
        var innerBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#0F172A"), 0), new GradientStop(Color.Parse("#1E3A8A"), 1) }
        };
        ctx.DrawRectangle(innerBrush, null, inner, screenR * 0.6, screenR * 0.6);

        // stand
        var standW = mw * 0.26;
        var standH = mh * 0.14;
        var neck = new Rect(mx + (mw - standW * 0.35) / 2, my + mh, standW * 0.35, standH * 0.9);
        ctx.DrawRectangle(Brushes.White, null, neck, s * 0.01, s * 0.01);
        var foot = new Rect(mx + (mw - standW) / 2, my + mh + standH * 0.8, standW, standH * 0.45);
        ctx.DrawRectangle(Brushes.White, null, foot, foot.Height / 2, foot.Height / 2);

        // signal arcs (remote connection) in the top-right of the screen
        var cx = inner.Right - inner.Width * 0.22;
        var cy = inner.Top + inner.Height * 0.48;
        var pen = new Pen(new SolidColorBrush(Color.Parse("#67E8F9")), s * 0.028, lineCap: PenLineCap.Round);
        for (var i = 1; i <= 3; i++)
        {
            var r = inner.Width * 0.09 * i;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(cx - r * 0.7071, cy + r * 0.7071), false);
                g.ArcTo(new Point(cx + r * 0.7071, cy - r * 0.7071), new Size(r, r), 0, false, SweepDirection.Clockwise);
                g.EndFigure(false);
            }
            ctx.DrawGeometry(null, pen, geo);
        }
        ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#67E8F9")), null, new Point(cx, cy), s * 0.022, s * 0.022);

        // window glyph on the left (tabs / sessions)
        var winRect = new Rect(inner.Left + inner.Width * 0.12, inner.Top + inner.Height * 0.3, inner.Width * 0.38, inner.Height * 0.42);
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), null, winRect, s * 0.012, s * 0.012);
        ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#2563EB")), null, new Rect(winRect.X, winRect.Y, winRect.Width, winRect.Height * 0.22), s * 0.012, s * 0.012);
        ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#2563EB")), null, new Rect(winRect.X, winRect.Y + winRect.Height * 0.12, winRect.Width, winRect.Height * 0.1));
        for (var i = 0; i < 3; i++)
            ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#93C5FD")), null, new Rect(winRect.X + winRect.Width * 0.12, winRect.Y + winRect.Height * (0.36 + i * 0.18), winRect.Width * (0.76 - i * 0.2), winRect.Height * 0.08), s * 0.004, s * 0.004);

        // cursor arrow
        var ax = inner.Left + inner.Width * 0.5;
        var ay = inner.Top + inner.Height * 0.58;
        var sc = inner.Width * 0.16;
        var cursor = new StreamGeometry();
        using (var g = cursor.Open())
        {
            g.BeginFigure(new Point(ax, ay), true);
            g.LineTo(new Point(ax, ay + sc));
            g.LineTo(new Point(ax + sc * 0.27, ay + sc * 0.77));
            g.LineTo(new Point(ax + sc * 0.46, ay + sc * 1.12));
            g.LineTo(new Point(ax + sc * 0.62, ay + sc * 1.04));
            g.LineTo(new Point(ax + sc * 0.44, ay + sc * 0.7));
            g.LineTo(new Point(ax + sc * 0.76, ay + sc * 0.7));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(Brushes.White, new Pen(new SolidColorBrush(Color.Parse("#0F172A")), s * 0.006, lineJoin: PenLineJoin.Round), cursor);
    }
}
