using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MRemoteMac.Models;

namespace MRemoteMac.Converters;

public static class ImageCache
{
    private static readonly Dictionary<string, Bitmap?> Cache = new();

    public static Bitmap? Get(string avaresPath)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(avaresPath, out var b)) return b;
            try
            {
                var uri = new Uri(avaresPath);
                b = AssetLoader.Exists(uri) ? new Bitmap(AssetLoader.Open(uri)) : null;
            }
            catch { b = null; }
            Cache[avaresPath] = b;
            return b;
        }
    }

    public static Bitmap? Image(string name) => Get($"avares://mRemoteMac/Assets/Images/{name}.png");

    public static Bitmap? ConnectionIcon(string? iconName)
        => Get($"avares://mRemoteMac/Assets/Icons/{(string.IsNullOrWhiteSpace(iconName) ? ConnectionInfo.DefaultIcon : iconName)}.png")
           ?? Get($"avares://mRemoteMac/Assets/Icons/{ConnectionInfo.DefaultIcon}.png");

    public static IEnumerable<string> AvailableIcons()
    {
        var uri = new Uri("avares://mRemoteMac/Assets/Icons/");
        return AssetLoader.GetAssets(uri, null)
            .Select(u => Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(u.AbsolutePath)))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>Tree node -> 16px icon (root, folder or the connection's own icon).</summary>
public sealed class NodeIconConverter : IValueConverter
{
    public static readonly NodeIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        RootNodeInfo => ImageCache.Image("ASPWebSite_16x"),
        ContainerInfo => ImageCache.Image("FolderClosed_16x"),
        ConnectionInfo c => ImageCache.ConnectionIcon(c.Icon),
        string s => ImageCache.ConnectionIcon(s),
        _ => null
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class AvaresImageConverter : IValueConverter
{
    public static readonly AvaresImageConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s ? ImageCache.Get(s) : null;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class GreaterThanZeroConverter : IValueConverter
{
    public static readonly GreaterThanZeroConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is int i && i > 0;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
