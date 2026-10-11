using Avalonia.Controls;

namespace MRemoteMac.Services;

/// <summary>
/// When the scripted UI audit runs (MRM_SCRIPT), windows must not steal the user's keyboard focus or cover the
/// desktop: they open without activation and far off-screen. Rendering and input injection are unaffected.
/// </summary>
public static class TestMode
{
    public static bool IsActive { get; } = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MRM_SCRIPT"))
                                           || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MRM_SCREENSHOT"))
                                           || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MRM_RENDER_ICON"));

    public static void Prepare(Window window)
    {
        if (!IsActive) return;
        // keep the user's keyboard focus where it is; windows still need to be on-screen for layout and hit-testing
        window.ShowActivated = false;
    }
}
