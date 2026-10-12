using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MRemoteMac.Models;
using MRemoteMac.Rdp;
using MRemoteMac.Services;

namespace MRemoteMac.ViewModels;

public enum TabState { Idle, Connecting, Connected, Disconnected, Failed }

/// <summary>
/// One open RDP session tab: owns the RdpSession, the embedded RdpView and the overlay shown while not connected.
/// All members are UI-thread affine unless noted; session events arrive on the RDP thread and are marshalled here.
/// </summary>
public sealed partial class ConnectionTab : ObservableObject, IAsyncDisposable
{
    public ConnectionInfo Info { get; }
    public RdpView View { get; } = new();
    public Panel Content { get; }
    public RdpSession? Session { get; private set; }

    private readonly AppSettings _settings;
    private readonly Window _owner;
    private readonly TextBlock _overlayText;
    private readonly Border _overlay;
    private readonly Controls.StaticNoiseView _noise = new() { IsVisible = false };
    private readonly Button _reconnectButton;
    private string? _lastLocalClipboard;
    private int _generation; // guards late events from a replaced session

    [ObservableProperty] private string _title;
    [ObservableProperty] private TabState _state = TabState.Idle;
    [ObservableProperty] private string _statusText = "";

    public bool ConnectToConsole { get; init; }
    public bool Fullscreen { get; init; }
    public bool WithoutCredentials { get; init; }

    public bool ViewOnly { get => View.ViewOnly; set => View.ViewOnly = value; }

    public bool SmartSize
    {
        get => View.ScaleMode == ViewScaleMode.SmartSize;
        set => View.ScaleMode = value ? ViewScaleMode.SmartSize : DefaultScaleMode();
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

        _overlayText = new TextBlock { Foreground = new SolidColorBrush(Color.Parse("#E5E8EC")), FontSize = 14, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, TextAlignment = TextAlignment.Center, LineHeight = 22 };
        _reconnectButton = new Button { Content = "Reconnect", Classes = { "dialog", "primary" }, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0), IsVisible = false };
        _reconnectButton.Click += (_, _) => AsyncHelper.Fire(ConnectAsync, $"Reconnect {Info.Name}");
        _overlay = new Border
        {
            Background = OverlayBrush(TabState.Idle),
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                Children = { _overlayText, _reconnectButton }
            }
        };
        Content = new Panel { Children = { View, _noise, _overlay } };
        View.GotFocus += (_, _) => AsyncHelper.Fire(PushLocalClipboardAsync, "Clipboard");
        View.FirstFrame += () => { if (State == TabState.Connected) SetState(TabState.Connected, ""); }; // lifts the overlay
    }

    private ViewScaleMode DefaultScaleMode()
    {
        // mRemoteNG semantics: FitToWindow / AutomaticResize = remote desktop follows the panel size (dynamic resolution);
        // SmartSize without automatic resize = fixed desktop scaled to fit.
        var resolution = Info.GetEffective<RdpResolutions>(nameof(ConnectionInfo.Resolution));
        var autoResize = Info.GetEffective<bool>(nameof(ConnectionInfo.AutomaticResize));
        if (resolution == RdpResolutions.FitToWindow || autoResize) return ViewScaleMode.FitToWindow;
        return ViewScaleMode.SmartSize;
    }

    // While connecting the TV static shows through a lighter scrim; otherwise the overlay is nearly opaque.
    private static readonly IBrush ConnectingScrim = new SolidColorBrush(Color.FromArgb(0x8C, 0x10, 0x14, 0x18));
    private static readonly IBrush SolidScrim = new SolidColorBrush(Color.FromArgb(0xE6, 0x10, 0x14, 0x18));
    private static IBrush OverlayBrush(TabState state) => state == TabState.Connecting ? ConnectingScrim : SolidScrim;

    private void SetState(TabState state, string text)
    {
        State = state;
        StatusText = text;
        // "Connected" arrives before the server has drawn anything; keep the overlay (and the static) up until the
        // first frame so the user never sees a black panel between the handshake and the desktop.
        var waitingForDesktop = state == TabState.Connected && !View.HasFrame;
        _overlayText.Text = waitingForDesktop ? $"Connecting to {Info.Name}..." : text;
        _overlay.IsVisible = state != TabState.Connected || waitingForDesktop;
        _noise.IsVisible = (state == TabState.Connecting || waitingForDesktop) && _settings.StaticWhileConnecting;
        _overlay.Background = OverlayBrush(_noise.IsVisible ? TabState.Connecting : TabState.Idle);
        _reconnectButton.IsVisible = state is TabState.Disconnected or TabState.Failed;
        StateChanged?.Invoke(this);
    }

    public async Task ConnectAsync()
    {
        await DisposeSessionAsync().ConfigureAwait(true);
        var generation = ++_generation;
        var session = new RdpSession(View);
        Session = session;
        View.Session = session;
        View.CommandKey = _settings.CommandKey;
        View.ScaleMode = DefaultScaleMode();

        var authLevel = Info.GetEffective<AuthenticationLevel>(nameof(ConnectionInfo.RdpAuthenticationLevel));
        var useCredSsp = Info.GetEffective<bool>(nameof(ConnectionInfo.UseCredSsp));

        session.VerifyCertificate = info =>
        {
            Notifications.Instance.Warn($"{Info.Name}: certificate for {info.Host} could not be verified, asking the user.");
            return Dialogs.Dialogs.Certificate(_owner, info, allowAccept: authLevel != AuthenticationLevel.AuthRequired);
        };
        session.Authenticate = req =>
        {
            // Without CredSSP the server shows its own logon screen (mstsc behaviour): pass through what we have.
            if (!useCredSsp && req.Reason is 1 or 2 && !WithoutCredentials)
                return Task.FromResult<Credentials?>(new Credentials(req.Username, req.Password, req.Domain));
            Notifications.Instance.Info($"{Info.Name}: credentials requested ({req.ReasonText}).");
            return Dialogs.Dialogs.CredentialsPrompt(_owner, req, Info.Name);
        };

        session.Connected += () => Dispatcher.UIThread.Post(() =>
        {
            if (generation != _generation) return;
            SetState(TabState.Connected, "");
            Info.OpenConnections++;
            View.Focus();
            Notifications.Instance.Info($"Connected to {Info.Name} ({Info.GetEffective<string>(nameof(ConnectionInfo.Hostname))})");
        });
        session.Disconnected += (err, text) => Dispatcher.UIThread.Post(() =>
        {
            if (generation != _generation) return;
            if (State == TabState.Connected && Info.OpenConnections > 0) Info.OpenConnections--;
            if (RdpSession.IsCleanDisconnect(err, session.LastErrorName))
            {
                var reason = err == 0 ? "" : $" ({text})";
                SetState(TabState.Disconnected, $"Disconnected from {Info.Name}.{reason}");
                Notifications.Instance.Info($"Disconnected from {Info.Name}{reason}");
                if (_settings.CloseTabOnDisconnect) CloseRequested?.Invoke(this);
            }
            else
            {
                SetState(TabState.Failed, $"Connection to {Info.Name} failed:\n{text}");
                Notifications.Instance.Error($"{Info.Name}: {text}");
            }
        });
        session.Reconnecting += () => Dispatcher.UIThread.Post(() =>
        {
            if (generation != _generation) return;
            SetState(TabState.Connecting, $"Connection to {Info.Name} lost, reconnecting...");
            Notifications.Instance.Warn($"{Info.Name}: connection lost, reconnecting.");
        });
        session.ClipboardTextReceived += text => Dispatcher.UIThread.Post(() => AsyncHelper.Fire(() => SetLocalClipboardAsync(text), "Clipboard"));

        SetState(TabState.Connecting, $"Connecting to {Info.Name}...");

        // let layout run so the view reports its real size for the initial desktop resolution
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
        var (w, h) = View.SuggestedDesktopSize();
        if (Fullscreen || Info.GetEffective<RdpResolutions>(nameof(ConnectionInfo.Resolution)) == RdpResolutions.Fullscreen)
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
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            SetState(TabState.Failed, ex.Message);
            Notifications.Instance.Error($"{Info.Name}: {ex.Message}");
        }
    }

    private async Task SetLocalClipboardAsync(string text)
    {
        var clip = TopLevel.GetTopLevel(View)?.Clipboard;
        if (clip is null) return;
        _lastLocalClipboard = text;
        await clip.SetTextAsync(text).ConfigureAwait(true);
    }

    private async Task PushLocalClipboardAsync()
    {
        if (Session is not { IsConnected: true } || !Info.GetEffective<bool>(nameof(ConnectionInfo.RedirectClipboard))) return;
        var clip = TopLevel.GetTopLevel(View)?.Clipboard;
        if (clip is null) return;
        var text = await clip.TryGetTextAsync().ConfigureAwait(true);
        if (!string.IsNullOrEmpty(text) && text != _lastLocalClipboard)
        {
            _lastLocalClipboard = text;
            Session.SetClipboardText(text);
        }
    }

    public void SendCtrlAltDel() => Session?.SendKeyCombination(ScanCodes.LeftControl, ScanCodes.LeftAlt, ScanCodes.Delete);
    public void SendCtrlEsc() => Session?.SendKeyCombination(ScanCodes.LeftControl, ScanCodes.Escape);
    public void SendWin() => Session?.SendKeyCombination(ScanCodes.LeftWin);
    public void SendAltTab() => Session?.SendKeyCombination(ScanCodes.LeftAlt, ScanCodes.Tab);

    /// <summary>Tears down the current session off the UI thread (the join may wait for the FreeRDP thread).</summary>
    public async Task DisposeSessionAsync()
    {
        var s = Session;
        if (s is null) return;
        _generation++;
        Session = null;
        View.Session = null;
        await Task.Run(() => { s.Disconnect(); s.Dispose(); }).ConfigureAwait(true);
        if (State == TabState.Connected && Info.OpenConnections > 0) Info.OpenConnections--;
        if (State == TabState.Connected) SetState(TabState.Disconnected, $"Disconnected from {Info.Name}.");
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeSessionAsync().ConfigureAwait(true);
        _noise.Dispose();
        View.DisposeBitmap();
    }
}
