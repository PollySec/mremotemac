using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace MRemoteMac.Controls;

/// <summary>
/// Shown over a session while files from the Mac are dragged onto it: an accent outline and a label saying what a drop
/// does, or why it cannot be done. It never takes the pointer, so the session underneath keeps receiving the drag.
/// </summary>
public sealed class FileDropHint : Border
{
    private readonly Icon _icon = new() { Size = 15, StrokeThickness = 2, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _text = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private bool? _allowed;

    public FileDropHint()
    {
        Name = "FileDropHint";
        IsHitTestVisible = false;
        IsVisible = false;
        BorderThickness = new Thickness(3);
        var pill = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(16, 18, 16, 0),
            Padding = new Thickness(12, 7, 14, 7),
            CornerRadius = new CornerRadius(999),
            BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 8 24 0 #55000000, 0 1 2 0 #22000000"),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _icon, _text } },
        };
        pill[!BackgroundProperty] = new DynamicResourceExtension("SurfaceElevatedBrush");
        pill[!BorderBrushProperty] = new DynamicResourceExtension("BorderOnElevatedBrush");
        _text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextPrimaryBrush");
        Child = pill;
    }

    public void Show(string text, bool allowed)
    {
        _text.Text = text;
        if (_allowed != allowed)
        {
            _allowed = allowed;
            var brush = allowed ? "AccentBrush" : "StatusErrorBrush";
            _icon.Glyph = allowed ? "upload" : "circle-x";
            _icon[!Icon.ForegroundProperty] = new DynamicResourceExtension(brush);
            this[!BorderBrushProperty] = new DynamicResourceExtension(brush);
        }
        IsVisible = true;
    }

    public void Hide()
    {
        IsVisible = false;
        _allowed = null;
    }
}
