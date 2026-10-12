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
    private static StreamWriter? _writer;
    private static long _written;
    private static Timer? _flushTimer;

    public static void Start(string version)
    {
        lock (Gate)
        {
            _started = true;
            WriteLine("-----", $"mRemoteMac {version} starting, pid {Environment.ProcessId}, {Environment.OSVersion.VersionString}, {RuntimeInformationText()}");
            // lines are buffered and flushed every half second (and at once for warnings and errors), so a busy
            // transfer that logs hundreds of lines does not pay for a file open per line
            _flushTimer = new Timer(_ => Flush(), null, 500, 500);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
        }
    }

    public static void Info(string text) => Write("info", text, false);
    public static void Warn(string text) => Write("warn", text, true);
    public static void Error(string text) => Write("error", text, true);
    /// <summary>File only; never shown in the Notifications panel.</summary>
    public static void Trace(string text) => Write("trace", text, false);

    public static void Flush()
    {
        lock (Gate)
        {
            try { _writer?.Flush(); }
            catch (IOException) { /* nothing to do */ }
        }
    }

    private static void Write(string level, string text, bool flush)
    {
        lock (Gate)
        {
            if (!_started) return;
            WriteLine(level, text);
            if (flush)
            {
                try { _writer?.Flush(); }
                catch (IOException) { /* nothing to do */ }
            }
        }
    }

    private static void WriteLine(string level, string text)
    {
        try
        {
            if (_writer is null || _written > RotateAt)
            {
                _writer?.Dispose();
                _writer = null;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var info = new FileInfo(Path);
                if (info.Exists && info.Length > RotateAt) File.Move(Path, Path + ".1", overwrite: true);
                _writer = new StreamWriter(new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                _written = new FileInfo(Path).Length;
            }
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {text}";
            _writer.WriteLine(line);
            _written += line.Length + 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* logging must never break the app */ }
    }

    private static string RuntimeInformationText() =>
        $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}";
}
