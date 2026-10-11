using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MRemoteMac.Controls;

/// <summary>
/// Analogue TV static ("myrornas krig") shown behind the overlay while a session connects.
/// A small grayscale <see cref="WriteableBitmap"/> (one cell per <see cref="CellSize"/> device pixels, capped at
/// <see cref="MaxCells"/>) is refilled from a xorshift generator at <see cref="FrameInterval"/> and drawn scaled
/// without interpolation, so the grain stays coarse and the per-frame cost stays well under a millisecond.
/// The timer runs only while the control is visible and attached, so idle tabs cost nothing.
/// </summary>
public sealed class StaticNoiseView : Control, IDisposable
{
    private const int CellSize = 3;
    private const int MaxCells = 640;
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(40); // 25 fps, like the real thing

    private readonly DispatcherTimer _timer;
    private WriteableBitmap? _bitmap;
    private ulong _rng = 0x9E3779B97F4A7C15UL;
    private int _bandRow;

    public StaticNoiseView()
    {
        IsHitTestVisible = false;
        _timer = new DispatcherTimer(FrameInterval, DispatcherPriority.Render, (_, _) => NextFrame());
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        UpdateTimer();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty) UpdateTimer();
    }

    private void UpdateTimer()
    {
        var run = IsVisible && this.GetVisualRoot() is not null;
        if (run && !_timer.IsEnabled) { NextFrame(); _timer.Start(); }
        else if (!run && _timer.IsEnabled) _timer.Stop();
    }

    private void NextFrame()
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var w = Math.Clamp((int)Math.Ceiling(Bounds.Width * scaling / CellSize), 1, MaxCells);
        var h = Math.Clamp((int)Math.Ceiling(Bounds.Height * scaling / CellSize), 1, MaxCells);
        if (_bitmap is null || _bitmap.PixelSize.Width != w || _bitmap.PixelSize.Height != h)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        }
        Fill(_bitmap, w, h);
        InvalidateVisual();
    }

    private unsafe void Fill(WriteableBitmap bmp, int w, int h)
    {
        using var fb = bmp.Lock();
        var basePtr = (byte*)fb.Address;
        var rng = _rng;
        _bandRow = (_bandRow + 2) % (h + 24); // a faint bar rolling down, as on a set with poor sync
        for (var y = 0; y < h; y++)
        {
            var row = (uint*)(basePtr + y * fb.RowBytes);
            var gain = (y & 1) == 0 ? 176 : 128;              // scan lines
            var d = Math.Abs(y - _bandRow);
            if (d < 12) gain += (12 - d) * 6;                 // rolling bar, max +72
            for (var x = 0; x < w; x += 2)
            {
                rng ^= rng << 13; rng ^= rng >> 7; rng ^= rng << 17; // xorshift64
                var g0 = (uint)(((rng & 0xFF) * (uint)gain) >> 8);
                var g1 = (uint)((((rng >> 8) & 0xFF) * (uint)gain) >> 8);
                row[x] = 0xFF000000u | (g0 << 16) | (g0 << 8) | g0;
                if (x + 1 < w) row[x + 1] = 0xFF000000u | (g1 << 16) | (g1 << 8) | g1;
            }
        }
        _rng = rng;
    }

    public override void Render(DrawingContext context)
    {
        var bmp = _bitmap;
        if (bmp is null) return;
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
            context.DrawImage(bmp, new Rect(bmp.Size), new Rect(Bounds.Size));
    }

    public void Dispose()
    {
        _timer.Stop();
        _bitmap?.Dispose();
        _bitmap = null;
    }
}
