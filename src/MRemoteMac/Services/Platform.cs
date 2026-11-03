using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;

namespace MRemoteMac.Services;

/// <summary>Everything that differs between the macOS and the Windows build, so the rest of the app can stay the same.</summary>
public static class Platform
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsMac => OperatingSystem.IsMacOS();

    /// <summary>The modifier of the app's own shortcuts: Cmd on the Mac, Ctrl on Windows.</summary>
    public static KeyModifiers CommandModifier => IsWindows ? KeyModifiers.Control : KeyModifiers.Meta;

    /// <summary>The file manager as it reads inside a sentence.</summary>
    public static string FileManager => IsWindows ? "File Explorer" : "the Finder";

    /// <summary>Where the key of the connection database is kept, as it reads inside a sentence.</summary>
    public static string KeyStoreName => IsWindows ? "Windows Credential Manager" : "your login keychain";

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string LocalData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mRemoteMac");

    /// <summary>
    /// Settings and the connection database. Local on Windows rather than roaming: the database key stays on this PC.
    /// </summary>
    public static string DataDirectory => IsWindows ? LocalData : Path.Combine(Home, "Library", "Application Support", "mRemoteMac");

    public static string LogDirectory => IsWindows ? Path.Combine(LocalData, "Logs") : Path.Combine(Home, "Library", "Logs", "mRemoteMac");

    public static string CacheDirectory => IsWindows ? Path.Combine(LocalData, "Cache") : Path.Combine(Home, "Library", "Caches", "mRemoteMac");

    /// <summary>Opens a folder in the file manager, or a URL in the browser.</summary>
    public static void Open(string target)
    {
        Process.Start(IsWindows
            ? new ProcessStartInfo(target) { UseShellExecute = true }
            : new ProcessStartInfo("open", [target]) { UseShellExecute = false });
    }

    /// <summary>
    /// A local path as the session should see it. APFS keeps names as typed (often NFD) and Windows expects composed
    /// characters; APFS lookups ignore the difference, NTFS names are used exactly as they are.
    /// </summary>
    public static string SessionPath(string path) => IsMac ? path.Normalize(NormalizationForm.FormC) : path;

    /// <summary>The menus are written with Cmd; on Windows the same gestures use Ctrl.</summary>
    public static void AdaptMenuGestures(ItemCollection items)
    {
        if (!IsWindows) return;
        foreach (var item in items.OfType<MenuItem>())
        {
            if (item.HotKey is { } hotKey) item.HotKey = WithCtrl(hotKey);
            if (item.InputGesture is { } gesture) item.InputGesture = WithCtrl(gesture);
            AdaptMenuGestures(item.Items);
        }
    }

    private static KeyGesture WithCtrl(KeyGesture gesture)
        => gesture.KeyModifiers.HasFlag(KeyModifiers.Meta) ? new KeyGesture(gesture.Key, (gesture.KeyModifiers & ~KeyModifiers.Meta) | KeyModifiers.Control) : gesture;
}
