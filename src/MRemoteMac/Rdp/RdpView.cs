using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace MRemoteMac.Rdp;

public enum ViewScaleMode { SmartSize, FitToWindow, Native }

/// <summary>
/// Embedded remote-desktop surface. Pixels arrive on the RDP thread through <see cref="IRdpFrameTarget"/> and are
/// written straight into a <see cref="WriteableBitmap"/> (single copy); the UI thread only invalidates and draws.
/// </summary>
public sealed class RdpView : Control, IRdpFrameTarget
{
    private RdpSession? _session;
    private volatile WriteableBitmap? _bitmap;   // written on the RDP thread, drawn on the UI thread
    private readonly object _swapLock = new();  // guards _bitmap replacement vs. producer writes
    private int _presentPending;
    private readonly DispatcherTimer _resizeTimer;
    private Cursor? _remoteCursor;
    private bool _cursorHidden;
    private int _lastMouseX = -1, _lastMouseY = -1;
    private readonly HashSet<uint> _pressedKeys = [];
    private long _frames;

    public static readonly StyledProperty<ViewScaleMode> ScaleModeProperty =
        AvaloniaProperty.Register<RdpView, ViewScaleMode>(nameof(ScaleMode), ViewScaleMode.FitToWindow);

    public ViewScaleMode ScaleMode
    {
        get => GetValue(ScaleModeProperty);
        set => SetValue(ScaleModeProperty, value);
    }

    public CommandKeyMode CommandKey { get; set; } = CommandKeyMode.Control;
    public bool ViewOnly { get; set; }

    /// <summary>Last mouse or keyboard activity in the view (UI thread); drives the idle timeout.</summary>
    public DateTime LastInputUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>Frames presented so far (diagnostics).</summary>
    public long FrameCount => Interlocked.Read(ref _frames);

    /// <summary>True once the current session has presented at least one frame (UI thread).</summary>
    public bool HasFrame { get; private set; }

    /// <summary>Raised on the UI thread when the current session presents its first frame.</summary>
    public event Action? FirstFrame;

    public RdpSession? Session
    {
        get => _session;
        set
        {
            if (_session is not null) Detach(_session);
            _session = value;
            HasFrame = false;
            if (_session is not null) Attach(_session);
            InvalidateVisual();
        }
    }

    static RdpView()
    {
        AffectsRender<RdpView>(ScaleModeProperty);
        FocusableProperty.OverrideDefaultValue<RdpView>(true);
    }

    public RdpView()
    {
        ClipToBounds = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased); // the desktop rectangle is pixel-aligned: skip AA edge work per frame
        // FreeRDP's own clients send at most one layout per 500 ms; the shim enforces the same, so retry until accepted
        _resizeTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        {
            if (RequestDesktopResize()) _resizeTimer!.Stop();
        });
    }

    private void Attach(RdpSession s)
    {
        s.PointerChanged += OnPointerChanged;
        s.PointerHidden += OnPointerHidden;
        s.Disconnected += OnDisconnected;
    }

    private void Detach(RdpSession s)
    {
        s.PointerChanged -= OnPointerChanged;
        s.PointerHidden -= OnPointerHidden;
        s.Disconnected -= OnDisconnected;
    }

    // ---------- IRdpFrameTarget (RDP thread) ----------

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        // Bgra8888 is Skia's native format (no transcoding); Opaque skips blending. Creating off the UI thread is fine.
        var fresh = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        WriteableBitmap? old;
        lock (_swapLock)
        {
            old = _bitmap;
            _bitmap = fresh;
        }
        // Bitmaps are ref-counted: the render thread keeps its own reference until the draw list is replaced.
        old?.Dispose();
    }

    public unsafe void Write(byte* src, int srcStride, int x, int y, int width, int height)
    {
        lock (_swapLock)
        {
            var bmp = _bitmap;
            if (bmp is null) return;
            var size = bmp.PixelSize;
            if (x < 0 || y < 0 || x + width > size.Width || y + height > size.Height) return;
            // Lock() excludes the render thread's Draw; keep it short: memcpy of the dirty rows only.
            using var fb = bmp.Lock();
            var rowBytes = width * 4;
            var dst = (byte*)fb.Address + (long)y * fb.RowBytes + x * 4;
            for (var row = 0; row < height; row++)
            {
                new ReadOnlySpan<byte>(src, rowBytes).CopyTo(new Span<byte>(dst, rowBytes));
                src += srcStride;
                dst += fb.RowBytes;
            }
        }
    }

    public void Present()
    {
        Interlocked.Increment(ref _frames);
        if (Interlocked.Exchange(ref _presentPending, 1) == 0)
            Dispatcher.UIThread.Post(OnPresentUi, DispatcherPriority.Render);
    }

    private void OnPresentUi()
    {
        Interlocked.Exchange(ref _presentPending, 0);
        InvalidateVisual();
        if (!HasFrame)
        {
            HasFrame = true;
            FirstFrame?.Invoke();
        }
    }

    private void OnDisconnected(uint err, string text) => Dispatcher.UIThread.Post(() => { Cursor = Cursor.Default; InvalidateVisual(); });

    // ---------- layout / render (UI thread) ----------

    private Rect DestRect(PixelSize px)
    {
        double bw = px.Width, bh = px.Height;
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (ScaleMode == ViewScaleMode.SmartSize)
        {
            var scale = Math.Min(Math.Min(w / bw, h / bh), 1.0); // never upscale, like mRemoteNG smart sizing
            var dw = Math.Floor(bw * scale);
            var dh = Math.Floor(bh * scale);
            return new Rect(Math.Floor((w - dw) / 2), Math.Floor((h - dh) / 2), dw, dh);
        }
        // FitToWindow: the remote desktop is kept at the panel size, so this is 1:1. Native: 1:1 top-left.
        return new Rect(0, 0, bw, bh);
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        var bmp = _bitmap;
        if (bmp is null) return;
        var dest = DestRect(bmp.PixelSize);
        var scaled = Math.Abs(dest.Width - bmp.PixelSize.Width) > 0.5;
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = scaled ? BitmapInterpolationMode.LowQuality : BitmapInterpolationMode.None }))
            context.DrawImage(bmp, new Rect(bmp.Size), dest);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (ScaleMode == ViewScaleMode.FitToWindow && _session?.IsConnected == true)
        {
            _resizeTimer.Stop();
            _resizeTimer.Start();
        }
        InvalidateVisual();
    }

    /// <summary>Asks the server to match the desktop to this control. Returns false when it must be retried later.</summary>
    private bool RequestDesktopResize()
    {
        if (_session is null || ScaleMode != ViewScaleMode.FitToWindow || !_session.IsConnected) return true;
        var (w, h) = SuggestedDesktopSize();
        var bmp = _bitmap;
        if (bmp is not null && bmp.PixelSize.Width == w && bmp.PixelSize.Height == h) return true;
        return _session.ResizeDesktop(w, h);
    }

    /// <summary>Desktop size to request for this control's current size (even width as RDP requires).</summary>
    public (int Width, int Height) SuggestedDesktopSize()
    {
        var w = (int)Bounds.Width & ~1;
        var h = (int)Bounds.Height;
        if (w < 200 || h < 200) return (1280, 800);
        return (w, h);
    }

    // ---------- pointer ----------

    private void OnPointerChanged(RemotePointer? ptr) => Dispatcher.UIThread.Post(() =>
    {
        _cursorHidden = false;
        if (ptr is null) { _remoteCursor = null; Cursor = Cursor.Default; return; }
        if (ptr.Cached is not Cursor cursor)
        {
            cursor = CreateCursor(ptr, TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0);
            ptr.Cached = cursor;
        }
        _remoteCursor = cursor;
        Cursor = cursor;
    });

    private void OnPointerHidden() => Dispatcher.UIThread.Post(() => { _cursorHidden = true; Cursor = new Cursor(StandardCursorType.None); });

    private static unsafe Cursor CreateCursor(RemotePointer p, double scaling)
    {
        fixed (byte* data = p.Bgra)
        {
            using var bmp = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Unpremul, (IntPtr)data, new PixelSize(p.Width, p.Height), new Vector(96, 96), p.Width * 4);
            // Avalonia passes the hotspot to NSCursor in points, so divide by the Retina scale (AvaloniaUI/Avalonia#6805).
            return new Cursor(bmp, new PixelPoint((int)Math.Round(p.HotX / scaling), (int)Math.Round(p.HotY / scaling)));
        }
    }

    private bool ToRemote(Point pt, out int rx, out int ry)
    {
        rx = ry = 0;
        var bmp = _bitmap;
        if (bmp is null) return false;
        var dest = DestRect(bmp.PixelSize);
        if (dest.Width <= 0 || dest.Height <= 0) return false;
        rx = Math.Clamp((int)Math.Round((pt.X - dest.X) * bmp.PixelSize.Width / dest.Width), 0, bmp.PixelSize.Width - 1);
        ry = Math.Clamp((int)Math.Round((pt.Y - dest.Y) * bmp.PixelSize.Height / dest.Height), 0, bmp.PixelSize.Height - 1);
        return true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        LastInputUtc = DateTime.UtcNow;
        base.OnPointerMoved(e);
        if (_session is null || ViewOnly) return;
        if (!ToRemote(e.GetPosition(this), out var x, out var y)) return;
        if (x == _lastMouseX && y == _lastMouseY) return;
        _lastMouseX = x; _lastMouseY = y;
        _session.SendMouse(RdpInput.PTR_FLAGS_MOVE, x, y);
        // 11.3.x may lose a programmatically set cursor (AvaloniaUI/Avalonia#19676): re-apply cheaply on move
        if (!_cursorHidden && _remoteCursor is not null && !ReferenceEquals(Cursor, _remoteCursor)) Cursor = _remoteCursor;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        LastInputUtc = DateTime.UtcNow;
        base.OnPointerPressed(e);
        Focus();
        if (_session is null || ViewOnly) return;
        var pt = e.GetCurrentPoint(this);
        if (!ToRemote(pt.Position, out var x, out var y)) return;
        SendButton(pt.Properties.PointerUpdateKind, true, x, y);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_session is null || ViewOnly) return;
        var pt = e.GetCurrentPoint(this);
        if (!ToRemote(pt.Position, out var x, out var y)) return;
        SendButton(pt.Properties.PointerUpdateKind, false, x, y);
        e.Handled = true;
    }

    private void SendButton(PointerUpdateKind kind, bool down, int x, int y)
    {
        if (_session is null) return;
        ushort flag = kind switch
        {
            PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => RdpInput.PTR_FLAGS_BUTTON1,
            PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => RdpInput.PTR_FLAGS_BUTTON2,
            PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => RdpInput.PTR_FLAGS_BUTTON3,
            _ => 0
        };
        if (flag != 0)
        {
            _session.SendMouse((ushort)(flag | (down ? RdpInput.PTR_FLAGS_DOWN : 0)), x, y);
            return;
        }
        ushort xflag = kind switch
        {
            PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton1Released => RdpInput.PTR_XFLAGS_BUTTON1,
            PointerUpdateKind.XButton2Pressed or PointerUpdateKind.XButton2Released => RdpInput.PTR_XFLAGS_BUTTON2,
            _ => 0
        };
        if (xflag != 0) _session.SendMouseExt((ushort)(xflag | (down ? RdpInput.PTR_XFLAGS_DOWN : 0)), x, y);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        LastInputUtc = DateTime.UtcNow;
        base.OnPointerWheelChanged(e);
        if (_session is null || ViewOnly) return;
        if (!ToRemote(e.GetPosition(this), out var x, out var y)) return;
        SendWheel(e.Delta.Y, false, x, y);
        SendWheel(e.Delta.X, true, x, y);
        e.Handled = true;
    }

    private void SendWheel(double delta, bool horizontal, int x, int y)
    {
        if (_session is null || Math.Abs(delta) < 0.01) return;
        // Avalonia: one notch = 1.0, trackpads give fractions. RDP: units of 120 per notch, 8-bit magnitude per PDU.
        var units = (int)Math.Round(delta * 120);
        if (units == 0) units = delta > 0 ? 1 : -1;
        // RDP: positive = up / right; AppKit (and Avalonia on macOS) report a positive X delta when scrolling left
        if (horizontal) units = -units;
        while (units != 0)
        {
            var step = Math.Clamp(units, -255, 255);
            units -= step;
            ushort flags = horizontal ? RdpInput.PTR_FLAGS_HWHEEL : RdpInput.PTR_FLAGS_WHEEL;
            var magnitude = Math.Abs(step);
            // the rotation is a 9-bit two's complement value: negative = NEGATIVE flag + (0x100 - magnitude)
            flags |= step < 0 ? (ushort)(RdpInput.PTR_FLAGS_WHEEL_NEGATIVE | (0x100 - magnitude)) : (ushort)magnitude;
            _session.SendMouse(flags, x, y);
        }
    }

    // ---------- keyboard ----------

    private uint? MapKey(KeyEventArgs e)
    {
        var pk = e.PhysicalKey;
        if (pk is PhysicalKey.MetaLeft or PhysicalKey.MetaRight)
        {
            var left = pk == PhysicalKey.MetaLeft;
            return CommandKey switch
            {
                CommandKeyMode.Control => left ? ScanCodes.LeftControl : ScanCodes.RightControl,
                CommandKeyMode.Alt => left ? ScanCodes.LeftAlt : ScanCodes.RightAlt,
                _ => left ? ScanCodes.LeftWin : ScanCodes.RightWin
            };
        }
        return ScanCodes.TryGet(pk, out var sc) ? sc : null;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        LastInputUtc = DateTime.UtcNow;
        if (_session is null || ViewOnly) { base.OnKeyDown(e); return; }
        var sc = MapKey(e);
        if (sc is { } code)
        {
            var repeat = !_pressedKeys.Add(code);
            _session.SendScancode(code, true, repeat);
            e.Handled = true;
            return;
        }
        if (!string.IsNullOrEmpty(e.KeySymbol))
        {
            foreach (var ch in e.KeySymbol) { _session.SendUnicode(ch, true); _session.SendUnicode(ch, false); }
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (_session is null || ViewOnly) { base.OnKeyUp(e); return; }
        var sc = MapKey(e);
        if (sc is { } code)
        {
            _pressedKeys.Remove(code);
            _session.SendScancode(code, false);
            e.Handled = true;
            return;
        }
        base.OnKeyUp(e);
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        ReleaseAllKeys();
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        _session?.SendFocusIn(0);
        if (_remoteCursor is not null && !_cursorHidden) Cursor = _remoteCursor;
    }

    public void ReleaseAllKeys()
    {
        if (_session is null) return;
        foreach (var k in _pressedKeys) _session.SendScancode(k, false);
        _pressedKeys.Clear();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _resizeTimer.Stop();
    }

    public void DisposeBitmap()
    {
        WriteableBitmap? old;
        lock (_swapLock) { old = _bitmap; _bitmap = null; }
        old?.Dispose();
    }
}
