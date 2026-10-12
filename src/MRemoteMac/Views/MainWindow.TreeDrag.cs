using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MRemoteMac.Models;
using MRemoteMac.ViewModels;

namespace MRemoteMac.Views;

/// <summary>
/// Drag and drop inside the connection tree: press on a node, move past the threshold and a ghost chip follows the
/// pointer while an insertion line (between rows) or a highlighted folder (into) shows where the node will land.
/// Hovering a collapsed folder opens it, the tree scrolls near its edges, Escape cancels. Pointer-driven rather than
/// the platform drag session, so it works identically under the scripted UI audit and needs no pasteboard round trip.
/// </summary>
public sealed partial class MainWindow
{
    private const double DragThreshold = 5;
    private const double RowEdgeFraction = 0.28; // top/bottom band of a folder row that means "before"/"after"

    private ConnectionInfo? _dragNode;
    private TreeViewItem? _dragItem;
    private Point _dragStart;
    private bool _dragArmed, _dragging;
    private TreeDropIndicator? _dropIndicator;
    private (ContainerInfo Parent, int Index)? _dropTarget;
    private ContainerInfo? _springFolder;
    private readonly DispatcherTimer _springTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };

    private void InitTreeDrag()
    {
        Tree.AddHandler(PointerPressedEvent, OnTreeDragPressed, RoutingStrategies.Tunnel);
        Tree.AddHandler(PointerMovedEvent, OnTreeDragMoved, RoutingStrategies.Tunnel);
        Tree.AddHandler(PointerReleasedEvent, OnTreeDragReleased, RoutingStrategies.Tunnel);
        Tree.AddHandler(PointerCaptureLostEvent, (_, _) => CancelTreeDrag());
        Tree.AddHandler(KeyDownEvent, (_, e) => { if (_dragging && e.Key == Key.Escape) { CancelTreeDrag(); e.Handled = true; } }, RoutingStrategies.Tunnel);
        _springTimer.Tick += (_, _) =>
        {
            _springTimer.Stop();
            if (_dragging && _springFolder is { IsExpanded: false } f) f.IsExpanded = true;
        };
    }

    private void OnTreeDragPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragArmed = false;
        if (!e.GetCurrentPoint(Tree).Properties.IsLeftButtonPressed || e.ClickCount != 1) return;
        var item = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        if (item?.DataContext is not ConnectionInfo node || node is RootNodeInfo || node.Parent is null) return;
        if ((e.Source as Visual)?.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null) return; // the chevron
        _dragNode = node;
        _dragItem = item;
        _dragStart = e.GetPosition(Tree);
        _dragArmed = true;
    }

    private void OnTreeDragMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragArmed || _dragNode is null) return;
        var pos = e.GetPosition(Tree);
        if (!_dragging)
        {
            if (Math.Abs(pos.X - _dragStart.X) < DragThreshold && Math.Abs(pos.Y - _dragStart.Y) < DragThreshold) return;
            if (!e.GetCurrentPoint(Tree).Properties.IsLeftButtonPressed) { _dragArmed = false; return; }
            BeginTreeDrag(e);
        }
        UpdateTreeDrag(pos);
        e.Handled = true;
    }

    private void OnTreeDragReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging)
        {
            _dragArmed = false;
            return;
        }
        var node = _dragNode;
        var target = _dropTarget;
        CancelTreeDrag();
        e.Handled = true;
        if (node is null || target is null) return;
        if (!_vm.MoveTo(node, target.Value.Parent, target.Value.Index)) return;
        // the TreeView may re-select a neighbour while it tears down the node's old container; settle on the moved
        // node after that has happened (same pattern as delete)
        _vm.SelectedNode = node;
        Dispatcher.UIThread.Post(() => { _vm.SelectedNode = node; FocusTree(); }, DispatcherPriority.Background);
        ScheduleAutoSave();
        Services.Notifications.Instance.Info($"Moved {node.Name} to {(target.Value.Parent is RootNodeInfo ? "the top level" : target.Value.Parent.Name)}");
    }

    private void BeginTreeDrag(PointerEventArgs e)
    {
        _dragging = true;
        e.Pointer.Capture(Tree);
        Tree.Cursor = new Cursor(StandardCursorType.DragMove);
        if (_dragItem is not null) _dragItem.Opacity = 0.45;
        // TreeOverlay is a sibling panel laid over the tree, so tree coordinates are overlay coordinates
        _dropIndicator = new TreeDropIndicator(_dragNode!.Name, IconNameFor(_dragNode), this, Tree);
        TreeOverlay.Children.Add(_dropIndicator);
    }

    private static string IconNameFor(ConnectionInfo node) => Converters.IconNames.ForNode(node);

    private void CancelTreeDrag()
    {
        _springTimer.Stop();
        _springFolder = null;
        if (_dragItem is not null) _dragItem.Opacity = 1;
        if (_dropIndicator is not null)
        {
            TreeOverlay.Children.Remove(_dropIndicator);
            _dropIndicator = null;
        }
        Tree.Cursor = null;
        _dragging = false;
        _dragArmed = false;
        _dragNode = null;
        _dragItem = null;
        _dropTarget = null;
    }

    private void UpdateTreeDrag(Point pos)
    {
        AutoScrollTree(pos);
        var item = (Tree.InputHitTest(pos) as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        var node = item?.DataContext as ConnectionInfo;
        (ContainerInfo Parent, int Index)? target = null;
        TreeDropIndicator.Shape shape = TreeDropIndicator.Shape.None;
        var where = default(Rect);
        ContainerInfo? spring = null;

        if (item is not null && node is not null && item.HeaderPresenter is { } header)
        {
            var origin = header.TranslatePoint(new Point(0, 0), Tree) ?? new Point(0, 0);
            var row = new Rect(origin.X, origin.Y, Math.Max(0, Tree.Bounds.Width - origin.X - 4), header.Bounds.Height);
            var rel = (pos.Y - row.Y) / Math.Max(1, row.Height);
            if (node is RootNodeInfo root)
            {
                target = (root, root.Children.Count);
                shape = TreeDropIndicator.Shape.Into;
                where = row;
            }
            else if (node is ContainerInfo folder && rel is >= RowEdgeFraction and <= 1 - RowEdgeFraction)
            {
                target = (folder, folder.Children.Count);
                shape = TreeDropIndicator.Shape.Into;
                where = row;
                spring = folder;
            }
            else if (node.Parent is { } parent)
            {
                var before = rel < 0.5;
                var index = parent.Children.IndexOf(node) + (before ? 0 : 1);
                if (!before && node is ContainerInfo { IsExpanded: true, Children.Count: > 0 } open)
                {
                    // "after" an open folder reads as "first inside it": the line sits under the folder row, indented
                    target = (open, 0);
                    where = new Rect(row.X + 20, row.Bottom, row.Width - 20, 0);
                }
                else
                {
                    target = (parent, index);
                    where = new Rect(row.X, before ? row.Y : row.Bottom, row.Width, 0);
                }
                shape = TreeDropIndicator.Shape.Line;
            }
        }
        else if (pos.Y > 0 && _vm.Root is { } root)
        {
            // below the last row: append to the top level
            target = (root, root.Children.Count);
            shape = TreeDropIndicator.Shape.None;
        }

        if (target is not null && _dragNode is not null && !MainViewModel.CanMoveTo(_dragNode, target.Value.Parent))
        {
            target = null;
            shape = TreeDropIndicator.Shape.Forbidden;
            where = item?.HeaderPresenter is { } h ? new Rect(h.TranslatePoint(new Point(0, 0), Tree) ?? default, h.Bounds.Size) : default;
        }
        if (target is { } t && _dragNode?.Parent == t.Parent)
        {
            // dropping a node right where it already is: show nothing to do
            var i = t.Parent.Children.IndexOf(_dragNode);
            if (t.Index == i || t.Index == i + 1) { target = null; shape = TreeDropIndicator.Shape.None; }
        }

        if (spring != _springFolder)
        {
            _springTimer.Stop();
            _springFolder = spring;
            if (spring is { IsExpanded: false }) _springTimer.Start();
        }
        _dropTarget = target;
        if (Services.TestMode.IsActive)
            Services.AppLog.Trace($"tree drag: {_dragNode?.Name} over {node?.Name ?? "(nothing)"} at {pos.X:0},{pos.Y:0} -> {shape} {(target is { } tt ? $"{tt.Parent.Name}[{tt.Index}]" : "-")} where {where}");
        _dropIndicator?.Update(shape, where, pos);
        Tree.Cursor = new Cursor(shape == TreeDropIndicator.Shape.Forbidden ? StandardCursorType.No : StandardCursorType.DragMove);
    }

    private void AutoScrollTree(Point pos)
    {
        var sv = Tree.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (sv is null) return;
        const double edge = 24;
        var step = 0.0;
        if (pos.Y < edge) step = -(edge - pos.Y);
        else if (pos.Y > Tree.Bounds.Height - edge) step = pos.Y - (Tree.Bounds.Height - edge);
        if (step != 0) sv.Offset = new Vector(sv.Offset.X, Math.Max(0, sv.Offset.Y + step * 0.6));
    }

    /// <summary>Draws the insertion line / folder highlight and the chip that follows the pointer.</summary>
    private sealed class TreeDropIndicator : Control
    {
        public enum Shape { None, Line, Into, Forbidden }

        private readonly string _label;
        private readonly Geometry? _icon;
        private readonly Control _theme;
        private readonly Control _tree;
        private Shape _shape;
        private Rect _where;
        private Point _pointer;
        private Rect _treeRect;

        public TreeDropIndicator(string label, string iconName, Control theme, Control tree)
        {
            _label = label;
            _icon = Controls.Icon.GeometryFor(iconName);
            _theme = theme;
            _tree = tree;
            IsHitTestVisible = false;
        }

        /// <summary>Coordinates are relative to the tree; they are shifted into this control's space when drawn.</summary>
        public void Update(Shape shape, Rect where, Point pointer)
        {
            _shape = shape;
            _where = where;
            _pointer = pointer;
            InvalidateVisual();
        }

        private IBrush Brush(string key, Color fallback) => _theme.TryFindResource(key, _theme.ActualThemeVariant, out var v) && v is IBrush b ? b : new SolidColorBrush(fallback);

        public override void Render(DrawingContext ctx)
        {
            var accent = Brush("AccentBrush", Color.Parse("#4464D3"));
            var subtle = Brush("AccentSubtleBrush", Color.Parse("#E1EBFF"));
            var error = Brush("StatusErrorBrush", Color.Parse("#BA3535"));
            var origin = _tree.TranslatePoint(new Point(0, 0), this) ?? new Point(0, 0);
            _treeRect = new Rect(origin, _tree.Bounds.Size);
            using var shift = ctx.PushTransform(Matrix.CreateTranslation(origin.X, origin.Y));
            using var clip = ctx.PushClip(new Rect(_tree.Bounds.Size));
            switch (_shape)
            {
                case Shape.Line:
                {
                    var pen = new Pen(accent, 2, lineCap: PenLineCap.Round);
                    var y = Math.Round(_where.Y) + 0.5;
                    ctx.DrawEllipse(accent, null, new Point(_where.X + 3, y), 3.5, 3.5);
                    ctx.DrawLine(pen, new Point(_where.X + 7, y), new Point(_where.Right, y));
                    break;
                }
                case Shape.Into:
                {
                    var r = new Rect(_where.X - 2, _where.Y, _where.Width + 2, _where.Height);
                    var fill = subtle is ISolidColorBrush sb ? new SolidColorBrush(sb.Color, 0.75) : subtle;
                    ctx.DrawRectangle(fill, new Pen(accent, 1.5), r, 6, 6);
                    break;
                }
                case Shape.Forbidden:
                {
                    if (_where.Width > 0)
                        ctx.DrawRectangle(null, new Pen(error, 1.5, dashStyle: DashStyle.Dash), new Rect(_where.X - 2, _where.Y, _where.Width + 2, _where.Height), 6, 6);
                    break;
                }
            }
            // the chip
            var text = new FormattedText(_label, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), 12.5, Brush("TextPrimaryBrush", Colors.Black));
            const double pad = 9, iconSize = 14, gap = 6;
            var w = pad + iconSize + gap + text.Width + pad;
            var h = 26.0;
            // below and to the right of the pointer, so the row under it and the insertion line stay readable
            var chip = new Rect(_pointer.X + 16, _pointer.Y + 12, w, h);
            if (chip.Right > _tree.Bounds.Width - 4) chip = chip.WithX(_tree.Bounds.Width - 4 - w);
            if (chip.Bottom > _tree.Bounds.Height - 4) chip = chip.WithY(_pointer.Y - 12 - h); // flip above near the bottom
            using var chipOpacity = ctx.PushOpacity(0.94);
            ctx.DrawRectangle(new SolidColorBrush(Colors.Black, 0.18), null, chip.Translate(new Vector(0, 2)), 8, 8);
            ctx.DrawRectangle(Brush("SurfaceElevatedBrush", Colors.White), new Pen(Brush("BorderOnElevatedBrush", Color.Parse("#E7EAED")), 1), chip, 8, 8);
            if (_icon is not null)
            {
                using (ctx.PushTransform(Matrix.CreateScale(iconSize / 24.0, iconSize / 24.0) * Matrix.CreateTranslation(chip.X + pad, chip.Y + (h - iconSize) / 2)))
                    ctx.DrawGeometry(null, new Pen(_shape == Shape.Forbidden ? error : accent, 1.75, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), _icon);
            }
            ctx.DrawText(text, new Point(chip.X + pad + iconSize + gap, chip.Y + (h - text.Height) / 2));
        }
    }
}
