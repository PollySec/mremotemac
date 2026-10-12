using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MRemoteMac.Rdp;
using MRemoteMac.Services;

namespace MRemoteMac.Controls;

/// <summary>
/// The card that slides into a session while files copied there are fetched to this Mac. It shows the items, their
/// progress and, above all, can be dragged into Finder (or any drop target) at any time: the drag carries file
/// promises, so the drop lands by itself the moment the transfer is done. Finder's Paste only works once the files
/// are in (it ignores promises), which the status line says.
/// </summary>
public sealed class FileTray : Border
{
    private static readonly TransformOperations Hidden = TransformOperations.Parse("translateY(16px)");
    private static readonly TransformOperations Shown = TransformOperations.Parse("translateY(0px)");

    private readonly Icon _kind = new() { Size = 22, StrokeThickness = 1.75, Glyph = "file" };
    private readonly Icon _stateIcon = new() { Size = 13, StrokeThickness = 2, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _status = new() { Classes = { "caption" }, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressBar _progress = new() { Height = 4, MinHeight = 4, Minimum = 0, Maximum = 1, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 7, 0, 0) };
    private readonly Button _close = new() { Classes = { "tray-close" }, Content = new Icon { Glyph = "x", Size = 13, StrokeThickness = 2 }, VerticalAlignment = VerticalAlignment.Top };
    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _autoHide; // the card leaves by itself a while after the files are ready

    private long _token;
    private bool _pressed;
    private Point _pressAt;
    private bool _finished;
    private int _items;
    private string _firstName = "";

    public FileTray()
    {
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(18);
        MaxWidth = 400;
        MinWidth = 280;
        CornerRadius = new CornerRadius(14);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(12, 11, 8, 11);
        BoxShadow = BoxShadows.Parse("0 12 32 0 #55000000, 0 1 2 0 #22000000");
        this[!BackgroundProperty] = new DynamicResourceExtension("SurfaceElevatedBrush");
        this[!BorderBrushProperty] = new DynamicResourceExtension("BorderOnElevatedBrush");
        Cursor = new Cursor(StandardCursorType.Hand);
        Name = "FileTray";
        _close.Name = "FileTrayClose";
        IsVisible = false;
        Opacity = 0;
        RenderTransform = Hidden;
        Transitions =
        [
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(200), Easing = new CubicEaseOut() },
            new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(280), Easing = new CubicEaseOut() },
        ];
        ToolTip.SetTip(this, "Drag this card to the Finder or any app, even while the files are still coming in; the drop completes when they are here.");

        var tile = new Border
        {
            Width = 42, Height = 42, CornerRadius = new CornerRadius(11), Child = _kind,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
        };
        tile[!BackgroundProperty] = new DynamicResourceExtension("AccentSubtleBrush");
        _kind[!Icon.ForegroundProperty] = new DynamicResourceExtension("AccentTextBrush");
        _kind.HorizontalAlignment = HorizontalAlignment.Center;
        _kind.VerticalAlignment = VerticalAlignment.Center;

        var grip = new Icon { Glyph = "grip-vertical", Size = 14, StrokeThickness = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        grip[!Icon.ForegroundProperty] = new DynamicResourceExtension("TextDisabledBrush");

        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(0, 2, 0, 0), Children = { _stateIcon, _status } };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _title, statusRow, _progress } };
        _progress[!ProgressBar.ForegroundProperty] = new DynamicResourceExtension("AccentBrush");
        _progress[!ProgressBar.BackgroundProperty] = new DynamicResourceExtension("BorderSubtleBrush");

        _close.Click += (_, _) => Hide();
        ToolTip.SetTip(_close, "Dismiss");

        Child = new DockPanel
        {
            Children = { Dock(grip, Avalonia.Controls.Dock.Left), Dock(tile, Avalonia.Controls.Dock.Left), Dock(_close, Avalonia.Controls.Dock.Right), text },
        };
        _hideTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => OnHideTimer());
        _autoHide = new DispatcherTimer(TimeSpan.FromSeconds(90), DispatcherPriority.Background, (_, _) => OnAutoHide());
    }

    private void OnAutoHide()
    {
        if (IsPointerOver || _pressed) return; // the user is about to drag it: stay
        _autoHide.Stop();
        Hide();
    }

    private void OnHideTimer()
    {
        _hideTimer.Stop();
        if (Opacity == 0) IsVisible = false;
    }

    private static Control Dock(Control c, Avalonia.Controls.Dock dock)
    {
        DockPanel.SetDock(c, dock);
        return c;
    }

    /// <summary>Shows the card for a transfer whose promises are identified by <paramref name="token"/>.</summary>
    public void Begin(long token, IReadOnlyList<(bool IsDirectory, string Name)> items, ulong totalBytes)
    {
        _token = token;
        _finished = false;
        _items = items.Count;
        _firstName = items.Count > 0 ? items[0].Name : "";
        _title.Text = items.Count switch
        {
            0 => "Files from the session",
            1 => _firstName,
            _ => $"{_firstName} and {items.Count - 1} more",
        };
        _kind.Glyph = items.Count > 1 ? "boxes" : GlyphFor(items.Count == 1 ? items[0] : (false, ""));
        _stateIcon.IsVisible = false;
        _progress.IsVisible = true;
        _progress.Value = 0;
        _status.Text = totalBytes > 0 ? $"Fetching · 0 of {Mb(totalBytes)} MB · drag me to the Finder now" : "Fetching · drag me to the Finder now";
        _hideTimer.Stop();
        _autoHide.Stop();
        IsVisible = true;
        Dispatcher.UIThread.Post(() => { Opacity = 1; RenderTransform = Shown; }, DispatcherPriority.Background);
    }

    public void Progress(ulong done, ulong total, string currentName)
    {
        if (_finished || !IsVisible) return;
        _progress.Value = total > 0 ? Math.Min(1.0, (double)done / total) : 0;
        var where = _items > 1 && !string.IsNullOrEmpty(currentName) ? $" · {currentName}" : "";
        _status.Text = $"Fetching · {Mb(done)} of {Mb(total)} MB{where}";
    }

    public void Finish(int files, string? error)
    {
        if (!IsVisible) return;
        _finished = true;
        _progress.IsVisible = false;
        _stateIcon.IsVisible = true;
        if (error is null)
        {
            _stateIcon.Glyph = "circle-check";
            _stateIcon[!Icon.ForegroundProperty] = new DynamicResourceExtension("StatusSuccessBrush");
            _status.Text = "Ready · drag to the Finder, or ⌘V there";
            _autoHide.Start();
        }
        else
        {
            _stateIcon.Glyph = "circle-x";
            _stateIcon[!Icon.ForegroundProperty] = new DynamicResourceExtension("StatusErrorBrush");
            _status.Text = error;
        }
    }

    public void Hide()
    {
        _autoHide.Stop();
        if (!IsVisible) return;
        Opacity = 0;
        RenderTransform = Hidden;
        _hideTimer.Start();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Source is Visual v && (v == _close || _close.IsVisualAncestorOf(v))) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _pressed = true;
        _pressAt = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _pressAt.X) < 4 && Math.Abs(p.Y - _pressAt.Y) < 4) return;
        _pressed = false;
        e.Pointer.Capture(null);
        StartDrag();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _pressed = false;
    }

    /// <summary>Hands the drag to AppKit: the native session carries the file promises and shows Finder-style icons.</summary>
    private void StartDrag()
    {
        var handle = TopLevel.GetTopLevel(this)?.TryGetPlatformHandle() as IMacOSTopLevelPlatformHandle;
        var view = handle?.NSView ?? IntPtr.Zero;
        if (_token == 0 || view == IntPtr.Zero)
        {
            AppLog.Warn($"file tray: cannot start a drag (token {_token}, view {view})");
            return;
        }
        var ok = Native.mr_promise_drag(_token, view) != 0;
        AppLog.Trace($"file tray: drag of '{_firstName}'{(_items > 1 ? $" (+{_items - 1})" : "")} {(ok ? "started" : "could not be started")}");
        if (!ok) Notifications.Instance.Warn("Clipboard: the drag could not be started; paste in Finder instead once the files are ready");
    }

    private static string Mb(ulong bytes) => (bytes / 1048576.0).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture);

    private static string GlyphFor((bool IsDirectory, string Name) item)
    {
        if (item.IsDirectory) return "folder";
        return Path.GetExtension(item.Name).ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".heic" or ".webp" or ".svg" => "image",
            ".mp4" or ".mov" or ".wmv" or ".avi" or ".mkv" or ".m4v" or ".webm" => "film",
            ".mp3" or ".wav" or ".aac" or ".flac" or ".m4a" or ".wma" => "music",
            ".zip" or ".7z" or ".rar" or ".gz" or ".tar" or ".tgz" or ".bz2" or ".xz" => "package",
            ".txt" or ".md" or ".log" or ".csv" or ".json" or ".xml" or ".rtf" or ".doc" or ".docx" or ".pdf" => "file-text",
            _ => "file",
        };
    }
}
