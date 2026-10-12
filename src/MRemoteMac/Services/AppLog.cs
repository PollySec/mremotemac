using System.Text;

namespace MRemoteMac.Services;

/// <summary>
/// Plain-text log at ~/Library/Logs/mRemoteMac/mRemoteMac.log. Every notification goes here, plus trace lines (FreeRDP's
/// clipboard diagnostics, pasteboard polling) that are too detailed for the Notifications panel. Rotated once at 5 MB.
/// </summary>
public static class AppLog
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "mRemoteMac", "mRemoteMac.log");

    private const long RotateAt = 5 * 1024 * 1024;
    private static readonly Lock Gate = new();
    private static bool _started;

    public static void Start(string version)
    {
        lock (Gate)
        {
            _started = true;
            WriteLine("-----", $"mRemoteMac {version} starting, pid {Environment.ProcessId}, {Environment.OSVersion.VersionString}, {RuntimeInformationText()}");
        }
    }

    public static void Info(string text) => Write("info", text);
    public static void Warn(string text) => Write("warn", text);
    public static void Error(string text) => Write("error", text);
    /// <summary>File only; never shown in the Notifications panel.</summary>
    public static void Trace(string text) => Write("trace", text);

    private static void Write(string level, string text)
    {
        lock (Gate)
        {
            if (!_started) return;
            WriteLine(level, text);
        }
    }

    private static void WriteLine(string level, string text)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var info = new FileInfo(Path);
            if (info.Exists && info.Length > RotateAt) File.Move(Path, Path + ".1", overwrite: true);
            File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {text}\n", Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* logging must never break the app */ }
    }

    private static string RuntimeInformationText() =>
        $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}";
}
