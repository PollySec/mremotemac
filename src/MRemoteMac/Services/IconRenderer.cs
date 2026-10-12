using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MRemoteMac.Controls;

namespace MRemoteMac.Services;

/// <summary>
/// Draws the application icon: macOS squircle with the Graphite + Indigo palette (gradient #688BF3 → #2F4ABA) and a
/// white display on a stand, centred and mirror-symmetric, with three centred bars on the screen and a green
/// "connected" LED in the foot.
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

        // ---- glyph: a display on a stand, centred on both axes; everything is mirror-symmetric ----
        var white = Brushes.White;
        var tint = new SolidColorBrush(Color.FromArgb(150, 0xC6, 0xD7, 0xFF));
        var mw = rect.Width * 0.60;
        var mh = mw * 0.64;
        var neckH = mh * 0.13;
        var footH = mh * 0.095;
        var total = mh + neckH + footH;
        var mx = rect.X + (rect.Width - mw) / 2;
        var my = rect.Y + (rect.Height - total) / 2 - s * 0.006; // optical centre sits a touch above geometric centre
        var screen = new Rect(mx, my, mw, mh);
        var r = mw * 0.08;
        var centerX = rect.X + rect.Width / 2;

        // glyph shadow (straight down, so it stays symmetric)
        for (var i = 3; i >= 1; i--)
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(14 + i * 6), 0x1A, 0x20, 0x50)), null,
                screen.Translate(new Vector(0, s * 0.006 * i)).Inflate(s * 0.003 * i), r, r);

        // bezel: white with a faint vertical sheen
        var bezel = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Colors.White, 0), new GradientStop(Color.Parse("#EEF2FF"), 1) }
        };
        ctx.DrawRectangle(bezel, null, screen, r, r);

        // face: deep indigo, vertical gradient
        var face = screen.Deflate(mw * 0.038);
        var faceBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#34509F"), 0), new GradientStop(Color.Parse("#1C2A6E"), 1) }
        };
        var fr = r * 0.6;
        ctx.DrawRectangle(faceBrush, null, face, fr, fr);
        // subtle glass reflection across the top of the face
        var glass = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.5, 0.45, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(34, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1) }
        };
        ctx.DrawRectangle(glass, null, face, fr, fr);

        // content: three centred bars, the top one bright (the active session), narrowing like a focused list
        var barH = face.Height * 0.11;
        var gap = face.Height * 0.10;
        var widths = new[] { 0.62, 0.46, 0.30 };
        var blockH = widths.Length * barH + (widths.Length - 1) * gap;
        var barY = face.Y + (face.Height - blockH) / 2;
        for (var i = 0; i < widths.Length; i++)
        {
            var bw = face.Width * widths[i];
            var br = new Rect(centerX - bw / 2, barY + i * (barH + gap), bw, barH);
            ctx.DrawRectangle(i == 0 ? white : tint, null, br, barH / 2, barH / 2);
        }

        // stand: neck and foot, both centred
        var neckW = mw * 0.12;
        ctx.DrawRectangle(white, null, new Rect(centerX - neckW / 2, my + mh, neckW, neckH + footH * 0.5), s * 0.006, s * 0.006);
        var footW = mw * 0.40;
        var foot = new Rect(centerX - footW / 2, my + mh + neckH, footW, footH);
        ctx.DrawRectangle(white, null, foot, footH / 2, footH / 2);

        // the connected LED: a green dot dead centre in the foot, ringed in indigo so it reads at 16 px
        var led = footH * 0.40;
        ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#5FBF78")), new Pen(new SolidColorBrush(Color.Parse("#2F4ABA")), led * 0.28), new Point(centerX, foot.Y + footH / 2), led, led);
    }
}
