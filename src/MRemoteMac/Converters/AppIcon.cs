using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace MRemoteMac.Converters;

/// <summary>The application icon (Assets/AppIcon.png), shared by windows and the About dialog.</summary>
public static class AppIcon
{
    private static readonly Lazy<Bitmap> LazyBitmap = new(() => new Bitmap(AssetLoader.Open(new Uri("avares://mRemoteMac/Assets/AppIcon.png"))));
    private static readonly Lazy<WindowIcon> LazyWindow = new(() => new WindowIcon(LazyBitmap.Value));

    public static Bitmap Bitmap => LazyBitmap.Value;
    public static WindowIcon Window => LazyWindow.Value;
}
