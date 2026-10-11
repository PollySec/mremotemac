using System.Globalization;
using Avalonia.Data.Converters;
using MRemoteMac.Models;

namespace MRemoteMac.Converters;

/// <summary>Maps mRemoteNG's connection icon names (kept in confCons.xml for compatibility) to Lucide icons.</summary>
public static class IconNames
{
    public const string Root = "globe";
    public const string Folder = "folder";
    public const string FolderOpen = "folder-open";
    public const string Default = "monitor";

    private static readonly Dictionary<string, string> MRemoteNgMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mRemoteNG"] = "monitor", ["mRemote"] = "monitor", ["Workstation"] = "monitor", ["Remote Desktop"] = "screen-share",
        ["Windows"] = "app-window", ["Apple"] = "laptop", ["Linux"] = "terminal", ["WSL"] = "terminal", ["PuTTY"] = "terminal",
        ["Telnet"] = "terminal", ["PowerShell"] = "terminal", ["Console"] = "square-terminal", ["SSH"] = "key-round",
        ["Admin"] = "user-cog", ["Anti Virus"] = "shield-alert", ["Firewall"] = "shield", ["Domain Controller"] = "shield-check",
        ["Backup"] = "archive", ["Build Server"] = "hammer", ["Database"] = "database", ["ESX"] = "boxes",
        ["Virtual Machine"] = "server-cog", ["Terminal Server"] = "server", ["Test Server"] = "test-tube", ["Staging"] = "flask-conical",
        ["Production"] = "factory", ["Infrastructure"] = "layers", ["File Server"] = "hard-drive", ["Web Server"] = "globe",
        ["Mail Server"] = "mail", ["SharePoint"] = "share-2", ["Fax"] = "printer", ["Finance"] = "banknote", ["Log"] = "scroll-text",
        ["RaspberryPi"] = "cpu", ["Router"] = "router", ["Switch"] = "network", ["WiFi"] = "wifi", ["Tel"] = "phone",
    };

    /// <summary>All selectable icon names (mRemoteNG names first, so files stay compatible, then the Lucide set).</summary>
    public static IReadOnlyList<string> Selectable { get; } =
        MRemoteNgMap.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Concat(LucideData.Paths.Keys.OrderBy(k => k, StringComparer.Ordinal))
            .ToList();

    public static string ForConnectionIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return Default;
        if (MRemoteNgMap.TryGetValue(icon, out var mapped)) return mapped;
        return LucideData.Paths.ContainsKey(icon) ? icon : Default;
    }

    public static string ForNode(ConnectionInfo node) => node switch
    {
        RootNodeInfo => Root,
        ContainerInfo c => c.IsExpanded ? FolderOpen : Folder,
        _ => ForConnectionIcon(node.Icon)
    };
}

/// <summary>Tree node -> Lucide icon name.</summary>
public sealed class NodeIconConverter : IValueConverter
{
    public static readonly NodeIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ConnectionInfo n => IconNames.ForNode(n),
        string s => IconNames.ForConnectionIcon(s),
        _ => IconNames.Default
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class GreaterThanZeroConverter : IValueConverter
{
    public static readonly GreaterThanZeroConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is int i && i > 0;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>MessageClass -> status brush from the active theme.</summary>
public sealed class MessageClassBrushConverter : IValueConverter
{
    public static readonly MessageClassBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            Services.MessageClass.Error => "StatusErrorBrush",
            Services.MessageClass.Warning => "StatusWarningBrush",
            _ => "StatusInfoBrush"
        };
        return Avalonia.Application.Current?.TryGetResource(key, Avalonia.Application.Current.ActualThemeVariant, out var brush) == true ? brush : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
