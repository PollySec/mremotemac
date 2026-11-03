namespace MRemoteMac.Services;

/// <summary>Writes unhandled exceptions to crash.log in the log folder so crashes can be diagnosed.</summary>
public static class CrashLog
{
    public static string Path { get; } = System.IO.Path.Combine(Platform.LogDirectory, "crash.log");

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Write("UnobservedTaskException", e.Exception); e.SetObserved(); };
    }

    public static void Write(string where, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.AppendAllText(Path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}: {ex}\n\n");
            Console.Error.WriteLine($"[crash] {where}: {ex}");
            AppLog.Error($"{where}: {ex}");
        }
        catch { /* nothing more we can do */ }
    }
}
