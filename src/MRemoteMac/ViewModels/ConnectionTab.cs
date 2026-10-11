using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MRemoteMac.Dialogs;
using MRemoteMac.Models;
using MRemoteMac.Rdp;
using MRemoteMac.Services;

namespace MRemoteMac.ViewModels;

public enum TabState { Idle, Connecting, Connected, Disconnected, Failed }

/// <summary>One open RDP session: owns the RdpSession, the RdpView and the overlay shown while not connected.</summary>
public sealed partial class ConnectionTab : ObservableObject
{
    public ConnectionInfo Info { get; }
    public RdpView View { get; } = new();
    public Panel Content { get; }
    public RdpSession? Session { get; private set; }
    private readonly AppSettings _settings;
    private readonly Window _owner;
    private readonly TextBlock _overlayText;
    private readonly Border _overlay;
    private readonly Button _reconnectButton;
    private string? _lastLocalClipboard;

    [ObservableProperty] private string _title;
    [ObservableProperty] private TabState _state = TabState.Idle;
    [ObservableProperty] private string _statusText = "";

    public bool ConnectToConsole { get; set; }
    public bool Fullscreen { get; set; }
    public bool WithoutCredentials { get; set; }
    public bool ViewOnly { get => View.ViewOnly; set => View.ViewOnly = value; }
    public bool SmartSize
    {
        get => View.ScaleMode == ViewScaleMode.SmartSize;
        set => View.ScaleMode = value ? ViewScaleMode.SmartSize : (Info.GetEffective<bool>(nameof(ConnectionInfo.AutomaticResize)) ? ViewScaleMode.FitToWindow : ViewScaleMode.Native);
    }

    public event Action<ConnectionTab>? CloseRequested;
    public event Action<ConnectionTab>? StateChanged;

    public bool IsConnected => State == TabState.Connected;

    public ConnectionTab(ConnectionInfo info, AppSettings settings, Window owner)
    {
        Info = info;
        _settings = settings;
        _owner = owner;
        _title = info.Name;

        _overlayText = new TextBlock { Foreground = Brushes.White, FontSize = 14, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, TextAlignment = TextAlignment.Center };
        _reconnectButton = new Button { Content = "Reconnect", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0), IsVisible = false };
        _reconnectButton.Click += async (_, _) => await ConnectAsync();
        _overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x1E, 0x1E, 0x1E)),
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                Children = { _overlayText, _reconnectButton }
            }
        };
        Content = new Panel { Children = { View, _overlay } };
        View.GotFocus += async (_, _) => await PushLocalClipboardAsync();
    }

    private void SetState(TabState state, string text)
    {
        void Do()
        {
            State = state;
            StatusText = text;
            _overlayText.Text = text;
            _overlay.IsVisible = state != TabState.Connected;
            _reconnectButton.IsVisible = state is TabState.Disconnected or TabState.Failed;
            StateChanged?.Invoke(this);
        }
        if (Dispatcher.UIThread.CheckAccess()) Do(); else Dispatcher.UIThread.Post(Do);
    }

    public async Task ConnectAsync()
    {
        await DisposeSessionAsync();
        var session = new RdpSession();
        Session = session;
        View.Session = session;
        View.CommandKey = _settings.CommandKey;

        var resolution = Info.GetEffective<RdpResolutions>(nameof(ConnectionInfo.Resolution));
        var autoResize = Info.GetEffective<bool>(nameof(ConnectionInfo.AutomaticResize));
        View.ScaleMode = resolution switch
        {
            RdpResolutions.SmartSize when !autoResize => ViewScaleMode.SmartSize,
            RdpResolutions.FitToWindow => ViewScaleMode.FitToWindow,
            _ => autoResize ? ViewScaleMode.FitToWindow : ViewScaleMode.SmartSize
        };

        var authLevel = Info.GetEffective<AuthenticationLevel>(nameof(ConnectionInfo.RdpAuthenticationLevel));
        session.VerifyCertificate = info =>
        {
            Notifications.Instance.Warn($"{Info.Name}: certificate for {info.Host} could not be verified, asking the user.");
            return Dialogs.Dialogs.Certificate(_owner, info, allowAccept: authLevel != AuthenticationLevel.AuthRequired);
        };
        var useCredSsp = Info.GetEffective<bool>(nameof(ConnectionInfo.UseCredSsp));
        session.Authenticate = req =>
        {
            // Without CredSSP (NLA) the server shows its own logon screen, like mstsc: pass through whatever we have.
            if (!useCredSsp && req.Reason is 1 or 2 && !WithoutCredentials)
                return Task.FromResult<Credentials?>(new Credentials(req.Username, req.Password, req.Domain));
            Notifications.Instance.Info($"{Info.Name}: credentials requested ({req.ReasonText}).");
            return Dialogs.Dialogs.CredentialsPrompt(_owner, req, Info.Name);
        };

        session.Connected += () =>
        {
            SetState(TabState.Connected, "");
            Dispatcher.UIThread.Post(() => { Info.OpenConnections++; View.Focus(); });
            Notifications.Instance.Info($"Connected to {Info.Name} ({Info.GetEffective<string>(nameof(ConnectionInfo.Hostname))})");
        };
        session.Disconnected += (err, text) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (State == TabState.Connected && Info.OpenConnections > 0) Info.OpenConnections--;
                if (err == 0)
                {
                    SetState(TabState.Disconnected, $"Disconnected from {Info.Name}.");
                    Notifications.Instance.Info($"Disconnected from {Info.Name}");
                    if (_settings.CloseTabOnDisconnect) CloseRequested?.Invoke(this);
                }
                else
                {
                    SetState(TabState.Failed, $"Connection to {Info.Name} failed:\n{text}");
                    Notifications.Instance.Error($"{Info.Name}: {text}");
                }
            });
        };
        session.ClipboardTextReceived += text => Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var clip = TopLevel.GetTopLevel(View)?.Clipboard;
                if (clip is not null) { _lastLocalClipboard = text; await clip.SetTextAsync(text); }
            }
            catch { /* clipboard unavailable */ }
        });

        SetState(TabState.Connecting, $"Connecting to {Info.Name}...");

        // wait for layout so the view has a real size to request as desktop resolution
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        var (w, h) = View.SuggestedDesktopSize();
        if (Fullscreen || resolution == RdpResolutions.Fullscreen)
        {
            var screen = _owner.Screens.ScreenFromWindow(_owner) ?? _owner.Screens.Primary;
            if (screen is not null) { w = screen.Bounds.Width & ~1; h = screen.Bounds.Height; }
        }

        try
        {
            RdpSettingsBuilder.Apply(session, Info, w, h,
                new RdpClientOptions { CommandKey = _settings.CommandKey, UseGfx = _settings.UseGfx },
                ConnectToConsole, WithoutCredentials);
            session.Connect();
        }
        catch (Exception ex)
        {
            SetState(TabState.Failed, ex.Message);
            Notifications.Instance.Error($"{Info.Name}: {ex.Message}");
        }
    }

    private async Task PushLocalClipboardAsync()
    {
        if (Session is null || !Session.IsConnected || !Info.GetEffective<bool>(nameof(ConnectionInfo.RedirectClipboard))) return;
        try
        {
            var clip = TopLevel.GetTopLevel(View)?.Clipboard;
            if (clip is null) return;
            var text = await clip.GetTextAsync();
            if (!string.IsNullOrEmpty(text) && text != _lastLocalClipboard)
            {
                _lastLocalClipboard = text;
                Session.SetClipboardText(text);
            }
        }
        catch { /* ignore */ }
    }

    public void SendCtrlAltDel() => Session?.SendKeyCombination(ScanCodes.LeftControl, ScanCodes.LeftAlt, ScanCodes.Delete);
    public void SendCtrlEsc() => Session?.SendKeyCombination(ScanCodes.LeftControl, ScanCodes.Escape);
    public void SendWin() => Session?.SendKeyCombination(ScanCodes.LeftWin);
    public void SendAltTab() => Session?.SendKeyCombination(ScanCodes.LeftAlt, ScanCodes.Tab);

    public async Task DisconnectAsync()
    {
        var s = Session;
        if (s is null) return;
        await Task.Run(s.Disconnect);
    }

    public async Task DisposeSessionAsync()
    {
        var s = Session;
        if (s is null) return;
        Session = null;
        View.Session = null;
        await Task.Run(() => { s.Disconnect(); s.Dispose(); });
        if (State == TabState.Connected && Info.OpenConnections > 0) Info.OpenConnections--;
    }
}
