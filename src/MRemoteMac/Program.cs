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
            // a 4K BGRA frame is 33 MB; the default ~28 MB Skia GPU cache would re-upload it every frame
            .With(new SkiaOptions { MaxGpuResourceSizeBytes = 256L * 1024 * 1024 })
            .With(new AvaloniaNativePlatformOptions { OverlayPopups = Environment.GetEnvironmentVariable("MRM_SCRIPT") is { Length: > 0 } })
            .WithInterFont()
            .LogToTrace();
}
