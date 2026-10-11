using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.Interactivity;

namespace MRemoteMac.Rdp;

public enum ViewScaleMode { SmartSize, FitToWindow, Native }

/// <summary>
/// Renders an RdpSession's framebuffer and forwards mouse/keyboard input.
/// </summary>
public sealed class RdpView : Control
{
    private RdpSession? _session;
    private WriteableBitmap? _bitmap;
    private int _pendingFrame;
    private readonly DispatcherTimer _resizeTimer;
    private Cursor? _remoteCursor;
    private bool _cursorHidden;
    private int _lastMouseX, _lastMouseY;
    private readonly HashSet<uint> _pressedKeys = new();
    private readonly HashSet<PointerUpdateKind> _pressedButtons = new();

    public static readonly StyledProperty<ViewScaleMode> ScaleModeProperty =
        AvaloniaProperty.Register<RdpView, ViewScaleMode>(nameof(ScaleMode), ViewScaleMode.SmartSize);

    public ViewScaleMode ScaleMode
    {
        get => GetValue(ScaleModeProperty);
        set => SetValue(ScaleModeProperty, value);
    }

    public CommandKeyMode CommandKey { get; set; } = CommandKeyMode.Control;
    public bool ViewOnly { get; set; }

    public RdpSession? Session
    {
        get => _session;
        set
        {
            if (_session is not null) Detach(_session);
            _session = value;
            if (_session is not null) Attach(_session);
            InvalidateVisual();
        }
    }

    public RdpView()
    {
        Focusable = true;
        ClipToBounds = true;
        Background = Brushes.Black;
        _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _resizeTimer.Tick += (_, _) =>
        {
            _resizeTimer.Stop();
            RequestDesktopResize();
        };
        AffectsRender<RdpView>(ScaleModeProperty);
    }

    public IBrush Background { get; set; }

    private void Attach(RdpSession s)
    {
        s.FrameDirty += OnFrameDirty;
        s.Resized += OnResized;
        s.PointerChanged += OnPointerChanged;
        s.PointerHidden += OnPointerHidden;
        s.Disconnected += OnDisconnected;
    }

    private void Detach(RdpSession s)
    {
        s.FrameDirty -= OnFrameDirty;
        s.Resized -= OnResized;
        s.PointerChanged -= OnPointerChanged;
        s.PointerHidden -= OnPointerHidden;
        s.Disconnected -= OnDisconnected;
    }

    // ---------- framebuffer -> bitmap (UI thread) ----------

    private void OnFrameDirty()
    {
        if (Interlocked.Exchange(ref _pendingFrame, 1) == 0)
            Dispatcher.UIThread.Post(FlushFrame, DispatcherPriority.Render);
    }

    private void OnResized(int w, int h) => OnFrameDirty();

    private void OnDisconnected(uint err, string text) => Dispatcher.UIThread.Post(() => { Cursor = Cursor.Default; InvalidateVisual(); });

    private unsafe void FlushFrame()
    {
        Interlocked.Exchange(ref _pendingFrame, 0);
        var s = _session;
        if (s is null) return;
        lock (s.BufferLock)
        {
            if (s.Width <= 0 || s.Height <= 0) return;
            if (_bitmap is null || _bitmap.PixelSize.Width != s.Width || _bitmap.PixelSize.Height != s.Height)
            {
                _bitmap?.Dispose();
                _bitmap = new WriteableBitmap(new PixelSize(s.Width, s.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                s.DirtyX0 = 0; s.DirtyY0 = 0; s.DirtyX1 = s.Width; s.DirtyY1 = s.Height;
            }
            if (s.DirtyX1 <= s.DirtyX0 || s.DirtyY1 <= s.DirtyY0) return;
            using (var fb = _bitmap.Lock())
            {
                var rowBytes = (s.DirtyX1 - s.DirtyX0) * 4;
                fixed (byte* src = s.Buffer)
                {
                    var dst = (byte*)fb.Address;
                    for (var y = s.DirtyY0; y < s.DirtyY1; y++)
                        Buffer.MemoryCopy(src + y * s.Stride + s.DirtyX0 * 4, dst + y * fb.RowBytes + s.DirtyX0 * 4, rowBytes, rowBytes);
                }
            }
            s.DirtyX0 = int.MaxValue; s.DirtyY0 = int.MaxValue; s.DirtyX1 = -1; s.DirtyY1 = -1;
        }
        InvalidateVisual();
    }

    // ---------- layout / render ----------

    private Rect DestRect()
    {
        if (_bitmap is null) return default;
        var bw = _bitmap.PixelSize.Width;
        var bh = _bitmap.PixelSize.Height;
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (ScaleMode == ViewScaleMode.SmartSize && (bw > w || bh > h || true))
        {
            var scale = Math.Min(w / bw, h / bh);
            if (scale > 1) scale = 1; // never upscale, like mRemoteNG smart sizing
            var dw = bw * scale;
            var dh = bh * scale;
            return new Rect((w - dw) / 2, (h - dh) / 2, dw, dh);
        }
        // native / fit: 1:1 top-left (fit keeps the remote desktop the size of this control)
        return new Rect(0, 0, bw, bh);
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Background, new Rect(Bounds.Size));
        if (_bitmap is null) return;
        var dest = DestRect();
        context.DrawImage(_bitmap, new Rect(_bitmap.Size), dest);
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

    private void RequestDesktopResize()
    {
        if (_session is null || ScaleMode != ViewScaleMode.FitToWindow) return;
        var scaling = (TopLevel.GetTopLevel(this)?.RenderScaling) ?? 1.0;
        var w = (int)(Bounds.Width);
        var h = (int)(Bounds.Height);
        _ = scaling;
        if (w < 200 || h < 200) return;
        if (_bitmap is not null && _bitmap.PixelSize.Width == (w & ~1) && _bitmap.PixelSize.Height == h) return;
        _session.ResizeDesktop(w, h);
    }

    /// <summary>Desktop size to request for a new connection given the current control size.</summary>
    public (int Width, int Height) SuggestedDesktopSize()
    {
        var w = (int)Bounds.Width;
        var h = (int)Bounds.Height;
        if (w < 640 || h < 480) return (1280, 800);
        return (w & ~1, h);
    }

    // ---------- pointer ----------

    private void OnPointerChanged(RemotePointer? ptr)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _cursorHidden = false;
            if (ptr is null) { _remoteCursor = null; Cursor = Cursor.Default; return; }
            if (ptr.Cached is not Cursor cursor)
            {
                cursor = CreateCursor(ptr);
                ptr.Cached = cursor;
            }
            _remoteCursor = cursor;
            Cursor = cursor;
        });
    }

    private void OnPointerHidden()
    {
        Dispatcher.UIThread.Post(() => { _cursorHidden = true; Cursor = new Cursor(StandardCursorType.None); });
    }

    private static unsafe Cursor CreateCursor(RemotePointer p)
    {
        fixed (byte* data = p.Bgra)
        {
            var bmp = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Unpremul, (IntPtr)data, new PixelSize(p.Width, p.Height), new Vector(96, 96), p.Width * 4);
            return new Cursor(bmp, new PixelPoint(p.HotX, p.HotY));
        }
    }

    private bool ToRemote(Point pt, out int rx, out int ry)
    {
        rx = ry = 0;
        if (_bitmap is null) return false;
        var dest = DestRect();
        if (dest.Width <= 0 || dest.Height <= 0) return false;
        var sx = _bitmap.PixelSize.Width / dest.Width;
        var sy = _bitmap.PixelSize.Height / dest.Height;
        rx = (int)Math.Round((pt.X - dest.X) * sx);
        ry = (int)Math.Round((pt.Y - dest.Y) * sy);
        rx = Math.Clamp(rx, 0, _bitmap.PixelSize.Width - 1);
        ry = Math.Clamp(ry, 0, _bitmap.PixelSize.Height - 1);
        return true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_session is null || ViewOnly) return;
        if (!ToRemote(e.GetPosition(this), out var x, out var y)) return;
        if (x == _lastMouseX && y == _lastMouseY) return;
        _lastMouseX = x; _lastMouseY = y;
        _session.SendMouse(RdpInput.PTR_FLAGS_MOVE, x, y);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (_session is null || ViewOnly) return;
        var pt = e.GetCurrentPoint(this);
        if (!ToRemote(pt.Position, out var x, out var y)) return;
        var kind = pt.Properties.PointerUpdateKind;
        _pressedButtons.Add(kind);
        SendButton(kind, true, x, y);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_session is null || ViewOnly) return;
        var pt = e.GetCurrentPoint(this);
        if (!ToRemote(pt.Position, out var x, out var y)) return;
        var kind = pt.Properties.PointerUpdateKind;
        SendButton(kind, false, x, y);
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
        if (xflag != 0)
            _session.SendMouseExt((ushort)(xflag | (down ? RdpInput.PTR_XFLAGS_DOWN : 0)), x, y);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
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
        // one notch = 120 units; macOS gives fractional deltas for trackpads
        var units = (int)Math.Round(delta * 120);
        if (units == 0) units = delta > 0 ? 1 : -1;
        while (units != 0)
        {
            var step = Math.Clamp(units, -255, 255);
            units -= step;
            ushort flags = horizontal ? RdpInput.PTR_FLAGS_HWHEEL : RdpInput.PTR_FLAGS_WHEEL;
            var magnitude = Math.Abs(step);
            // RDP: vertical positive = scroll up; horizontal positive = scroll right. Avalonia: Y positive = up, X positive = left? (X positive = right)
            var negative = horizontal ? step < 0 : step < 0;
            if (negative) flags |= RdpInput.PTR_FLAGS_WHEEL_NEGATIVE;
            flags |= (ushort)(magnitude & 0xFF);
            _session.SendMouse(flags, x, y);
        }
    }

    // ---------- keyboard ----------

    private uint? MapKey(KeyEventArgs e)
    {
        var pk = e.PhysicalKey;
        if (pk is PhysicalKey.MetaLeft or PhysicalKey.MetaRight)
        {
            return CommandKey switch
            {
                CommandKeyMode.Control => pk == PhysicalKey.MetaLeft ? ScanCodes.LeftControl : ScanCodes.RightControl,
                CommandKeyMode.Alt => pk == PhysicalKey.MetaLeft ? ScanCodes.LeftAlt : ScanCodes.RightAlt,
                _ => pk == PhysicalKey.MetaLeft ? ScanCodes.LeftWin : ScanCodes.RightWin
            };
        }
        return ScanCodes.TryGet(pk, out var sc) ? sc : null;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_session is null || ViewOnly) { base.OnKeyDown(e); return; }
        var sc = MapKey(e);
        if (sc is not null)
        {
            var repeat = _pressedKeys.Contains(sc.Value);
            _pressedKeys.Add(sc.Value);
            _session.SendScancode(sc.Value, true, repeat);
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
        if (sc is not null)
        {
            _pressedKeys.Remove(sc.Value);
            _session.SendScancode(sc.Value, false);
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
        _bitmap?.Dispose();
        _bitmap = null;
    }
}
