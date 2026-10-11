using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MRemoteMac.Controls;

namespace MRemoteMac.Services;

/// <summary>
/// Draws the application icon: macOS squircle with the Graphite + Indigo palette (gradient #688BF3 → #2F4ABA),
/// a white display glyph with session tabs, a connection arc and a green "connected" dot.
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
        // Apple grid: 824/1024 body, 100 px margin for the system shadow
        var inset = s * 0.0977;
        var rect = new Rect(inset, inset, s - 2 * inset, s - 2 * inset);
        var radius = rect.Width * 0.2237;

        // soft ambient shadow
        for (var i = 8; i >= 1; i--)
        {
            var sh = rect.Inflate(i * s * 0.003).Translate(new Vector(0, i * s * 0.004));
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(6 + i), 0x1A, 0x20, 0x50)), null, sh, radius, radius);
        }

        var body = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse("#688BF3"), 0),
                new GradientStop(Color.Parse("#4464D3"), 0.55),
                new GradientStop(Color.Parse("#2F4ABA"), 1)
            }
        };
        ctx.DrawRectangle(body, null, rect, radius, radius);

        // top inner highlight + bottom shade (Big Sur style)
        var highlight = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5, 0.12, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(46, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1) }
        };
        ctx.DrawRectangle(highlight, null, rect, radius, radius);
        var shade = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5, 0.9, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(0, 0x1A, 0x20, 0x50), 0), new GradientStop(Color.FromArgb(50, 0x1A, 0x20, 0x50), 1) }
        };
        ctx.DrawRectangle(shade, null, rect, radius, radius);
        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(64, 255, 255, 255)), s * 0.004), rect.Deflate(s * 0.002), radius, radius);

        // ---- glyph: a display with session tabs ----
        var white = Brushes.White;
        var tint = new SolidColorBrush(Color.Parse("#C6D7FF"));
        var mw = rect.Width * 0.58;
        var mh = mw * 0.66;
        var mx = rect.X + (rect.Width - mw) / 2;
        var my = rect.Y + rect.Height * 0.22;
        var screen = new Rect(mx, my, mw, mh);
        var r = mw * 0.07;

        // glyph shadow
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(60, 0x1A, 0x20, 0x50)), null, screen.Translate(new Vector(0, s * 0.012)).Inflate(s * 0.004), r, r);
        // bezel + face
        ctx.DrawRectangle(white, null, screen, r, r);
        var face = screen.Deflate(mw * 0.035);
        var faceBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#2F4ABA"), 0), new GradientStop(Color.Parse("#1E2D7A"), 1) }
        };
        ctx.DrawRectangle(faceBrush, null, face, r * 0.65, r * 0.65);

        // tab strip inside the face
        var tabY = face.Y + face.Height * 0.09;
        var tabH = face.Height * 0.14;
        var tabW = face.Width * 0.22;
        var tabX = face.X + face.Width * 0.08;
        for (var i = 0; i < 3; i++)
        {
            var isActive = i == 0;
            var tr = new Rect(tabX + i * (tabW + face.Width * 0.03), tabY, tabW, tabH);
            ctx.DrawRectangle(isActive ? white : new SolidColorBrush(Color.FromArgb(90, 0xC6, 0xD7, 0xFF)), null, tr, s * 0.012, s * 0.012);
        }
        // window content lines
        var lineY = tabY + tabH + face.Height * 0.12;
        for (var i = 0; i < 3; i++)
        {
            var lw = face.Width * (0.72 - i * 0.18);
            ctx.DrawRectangle(tint, null, new Rect(face.X + face.Width * 0.08, lineY + i * face.Height * 0.16, lw, face.Height * 0.07), s * 0.008, s * 0.008);
        }

        // stand
        var neckW = mw * 0.10;
        var neckH = mh * 0.12;
        ctx.DrawRectangle(white, null, new Rect(mx + (mw - neckW) / 2, my + mh, neckW, neckH), s * 0.008, s * 0.008);
        var footW = mw * 0.34;
        var footH = mh * 0.055;
        ctx.DrawRectangle(white, null, new Rect(mx + (mw - footW) / 2, my + mh + neckH - footH * 0.2, footW, footH), footH / 2, footH / 2);

        // connection arcs + dot at the lower right of the display (signal / live session)
        var cx = screen.Right - mw * 0.02;
        var cy = screen.Bottom - mh * 0.02;
        var arcPen = new Pen(white, s * 0.026, lineCap: PenLineCap.Round);
        for (var i = 1; i <= 2; i++)
        {
            var ar = mw * 0.11 * i;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(cx - ar, cy), false);
                g.ArcTo(new Point(cx, cy - ar), new Size(ar, ar), 0, false, SweepDirection.Clockwise);
                g.EndFigure(false);
            }
            ctx.DrawGeometry(null, arcPen, geo);
        }
        ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#2F4ABA")), null, new Point(cx, cy), s * 0.052, s * 0.052);
        ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#69C27E")), new Pen(white, s * 0.012), new Point(cx, cy), s * 0.040, s * 0.040);
    }
}
