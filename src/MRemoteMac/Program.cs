using Avalonia;

namespace MRemoteMac;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Services.CrashLog.Install();
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception ex) { Services.CrashLog.Write("Main", ex); throw; }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new AvaloniaNativePlatformOptions { OverlayPopups = Environment.GetEnvironmentVariable("MRM_SCRIPT") is { Length: > 0 } })
            .WithInterFont()
            .LogToTrace();
}
