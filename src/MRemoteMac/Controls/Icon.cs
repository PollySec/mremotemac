using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MRemoteMac.Converters;

namespace MRemoteMac.Controls;

/// <summary>
/// Vector icon from the bundled Lucide set, drawn as a stroked geometry so it stays crisp at any scale and
/// takes its colour from <see cref="Foreground"/> (theme-aware). Geometries are parsed once and shared.
/// </summary>
public sealed class Icon : Control
{
    private static readonly Dictionary<string, Geometry?> Cache = new(StringComparer.Ordinal);

    public static readonly StyledProperty<string?> GlyphProperty = AvaloniaProperty.Register<Icon, string?>(nameof(Glyph));
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<Icon, double>(nameof(Size), 16);
    public static readonly StyledProperty<IBrush?> ForegroundProperty = Avalonia.Controls.Documents.TextElement.ForegroundProperty.AddOwner<Icon>();
    public static readonly StyledProperty<double> StrokeThicknessProperty = AvaloniaProperty.Register<Icon, double>(nameof(StrokeThickness), 1.75);

    static Icon()
    {
        AffectsRender<Icon>(GlyphProperty, ForegroundProperty, StrokeThicknessProperty);
        AffectsMeasure<Icon>(SizeProperty);
    }

    /// <summary>Lucide icon name, e.g. "monitor".</summary>
    public string? Glyph { get => GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    /// <summary>Stroke width in 24-unit icon space (Lucide default is 2; 1.75 reads lighter at 16 px).</summary>
    public double StrokeThickness { get => GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    public static Geometry? GeometryFor(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var g)) return g;
            g = LucideData.Paths.TryGetValue(name, out var data) ? StreamGeometry.Parse(data) : null;
            Cache[name] = g;
            return g;
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        var geometry = GeometryFor(Glyph);
        if (geometry is null || Foreground is null) return;
        var scale = Size / 24.0;
        var pen = new Pen(Foreground, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
            context.DrawGeometry(null, pen, geometry);
    }
}
