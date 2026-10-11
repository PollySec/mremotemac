using System.Collections.ObjectModel;
using Avalonia.Threading;

namespace MRemoteMac.Services;

public enum MessageClass { Information, Warning, Error }

public sealed record NotificationMessage(MessageClass Class, string Text, DateTime Time)
{
    public string IconName => Class switch
    {
        MessageClass.Error => "circle-x",
        MessageClass.Warning => "triangle-alert",
        _ => "info"
    };

    public string IconClass => Class switch { MessageClass.Error => "error", MessageClass.Warning => "warning", _ => "info" };
    public string TimeText => Time.ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);
    public string Display => $"{TimeText}  {Text}";
}

/// <summary>Collects status, warning and error messages for the notifications panel (mRemoteNG "Errors and Infos").</summary>
public sealed class Notifications
{
    public static Notifications Instance { get; } = new();
    public static bool EchoToConsole { get; set; } = Environment.GetEnvironmentVariable("MRM_LOG") == "1";
    public ObservableCollection<NotificationMessage> Messages { get; } = [];
    public event Action<NotificationMessage>? MessageAdded;

    public void Add(MessageClass cls, string text)
    {
        void Do()
        {
            var m = new NotificationMessage(cls, text, DateTime.Now);
            Messages.Add(m);
            if (EchoToConsole) Console.Error.WriteLine($"[{cls}] {text}");
            while (Messages.Count > 500) Messages.RemoveAt(0);
            MessageAdded?.Invoke(m);
        }
        if (Dispatcher.UIThread.CheckAccess()) Do(); else Dispatcher.UIThread.Post(Do);
    }

    public void Info(string text) => Add(MessageClass.Information, text);
    public void Warn(string text) => Add(MessageClass.Warning, text);
    public void Error(string text) => Add(MessageClass.Error, text);
}
