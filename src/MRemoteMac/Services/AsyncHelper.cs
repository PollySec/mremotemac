namespace MRemoteMac.Services;

/// <summary>
/// Bridges void UI event handlers to async work without <c>async void</c>: exceptions are observed, logged and
/// surfaced in the notifications panel instead of tearing the process down.
/// </summary>
public static class AsyncHelper
{
    public static void Fire(Func<Task> action, string context) => _ = ObserveAsync(action, context);

    private static async Task ObserveAsync(Func<Task> action, string context)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // cancelled by the user: nothing to report
        }
        catch (Exception ex)
        {
            CrashLog.Write(context, ex);
            Notifications.Instance.Error($"{context}: {ex.Message}");
        }
    }
}
