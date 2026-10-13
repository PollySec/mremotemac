using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
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
    private long _lastPasteboardChange = -1;
    private readonly DispatcherTimer _clipboardTimer;
    private readonly DispatcherTimer _idleTimer;
    private readonly Border _frame;
    private readonly Controls.FileTray _tray = new();
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
        _frame = new Border { Child = new Panel { Children = { View, _noise, _overlay, _tray } }, BorderThickness = new Thickness(0) };
        ApplyFrameColor();
        Content = new Panel { Children = { _frame } };
        _idleTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => CheckIdle());
        View.GotFocus += (_, _) => AsyncHelper.Fire(PushLocalClipboardAsync, "Clipboard");
        // macOS has no clipboard-change notification: poll NSPasteboard.changeCount (an integer read) while connected
        // and push new local text to the session, so copy on the Mac then paste in the session just works.
        _clipboardTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => PollLocalClipboard());
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
            _lastPasteboardChange = Native.mr_pasteboard_change_count();
            if (Info.GetEffective<bool>(nameof(ConnectionInfo.RedirectClipboard)))
            {
                _clipboardTimer.Start();
                // whatever was copied before the connection came up belongs to this session too: a new session has
                // nothing of ours yet, so forget what an earlier one was given (kept natively until MonitorReady)
                _lastLocalClipboard = null;
                AsyncHelper.Fire(PushLocalClipboardAsync, "Clipboard");
            }
            else
                Notifications.Instance.Warn($"{Info.Name}: Clipboard: sharing is off for this connection (Redirect Clipboard); copy and paste between the Mac and the session needs it turned on and a reconnect");
            if (Info.GetEffective<int>(nameof(ConnectionInfo.RdpMinutesToIdleTimeout)) > 0) _idleTimer.Start();
            Notifications.Instance.Info($"Connected to {Info.Name} ({Info.GetEffective<string>(nameof(ConnectionInfo.Hostname))})");
        });
        session.Disconnected += (err, text) => Dispatcher.UIThread.Post(() =>
        {
            if (generation != _generation) return;
            _clipboardTimer.Stop();
            _idleTimer.Stop();
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
        session.Diagnostic += (level, text) =>
        {
            if (level == 0) AppLog.Trace($"{Info.Name}: {text}");
            else Dispatcher.UIThread.Post(() => Notifications.Instance.Info($"{Info.Name}: {text}"));
        };
        session.ClipboardFilesOffered += (count, folders, total, preview) => Dispatcher.UIThread.Post(() => { if (generation == _generation) OnClipboardFilesOffered(count, folders, total, preview); });
        session.ClipboardFileProgress += (done, total, name) => Dispatcher.UIThread.Post(() => OnClipboardFileProgress(done, total, name));
        session.ClipboardFilesDone += (dest, files, error) => Dispatcher.UIThread.Post(() => AsyncHelper.Fire(() => OnClipboardFilesDoneAsync(dest, files, error), "Clipboard files"));

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
        _lastPasteboardChange = Native.mr_pasteboard_change_count(); // our own write must not be pushed back
        Notifications.Instance.Info($"{Info.Name}: Clipboard: {text.Length} characters from the session are on the Mac clipboard");
    }

    /// <summary>Colors for "Connection Frame Color"; the same hues the status palette uses so they fit both themes.</summary>
    private static readonly Dictionary<ConnectionFrameColor, Color> FrameColors = new()
    {
        [ConnectionFrameColor.Red] = Color.Parse("#D9534F"), [ConnectionFrameColor.Yellow] = Color.Parse("#E8B931"),
        [ConnectionFrameColor.Green] = Color.Parse("#3DA35D"), [ConnectionFrameColor.Blue] = Color.Parse("#4464D3"),
        [ConnectionFrameColor.Purple] = Color.Parse("#8E5BD1"),
    };

    /// <summary>Applies the connection's frame color (call again after the property changes).</summary>
    public void ApplyFrameColor()
    {
        var color = Info.GetEffective<ConnectionFrameColor>(nameof(ConnectionInfo.ConnectionFrameColor));
        var on = FrameColors.TryGetValue(color, out var c);
        _frame.BorderThickness = new Thickness(on ? 3 : 0);
        _frame.BorderBrush = on ? new SolidColorBrush(c) : null;
    }

    /// <summary>mRemoteNG semantics: after N minutes without input the session is closed, optionally with a message.</summary>
    private void CheckIdle()
    {
        var minutes = Info.GetEffective<int>(nameof(ConnectionInfo.RdpMinutesToIdleTimeout));
        if (minutes <= 0 || State != TabState.Connected) { _idleTimer.Stop(); return; }
        if (DateTime.UtcNow - View.LastInputUtc < TimeSpan.FromMinutes(minutes)) return;
        _idleTimer.Stop();
        AsyncHelper.Fire(async () =>
        {
            await DisposeSessionAsync().ConfigureAwait(true);
            SetState(TabState.Disconnected, $"Disconnected from {Info.Name} after {minutes} min without input.");
            Notifications.Instance.Info($"{Info.Name}: disconnected due to inactivity ({minutes} min).");
            if (Info.GetEffective<bool>(nameof(ConnectionInfo.RdpAlertIdleTimeout)))
                await Dialogs.Dialogs.Message(_owner, "Session Disconnected", $"The {Info.Name} session was disconnected due to inactivity.", Dialogs.Dialogs.MsgButtons.Ok);
        }, "Idle timeout");
    }

    private void PollLocalClipboard()
    {
        var count = Native.mr_pasteboard_change_count();
        if (count == _lastPasteboardChange) return;
        _lastPasteboardChange = count;
        AppLog.Trace($"{Info.Name}: pasteboard changed (change count {count})");
        AsyncHelper.Fire(PushLocalClipboardAsync, "Clipboard");
    }

    private async Task PushLocalClipboardAsync()
    {
        if (Session is not { IsConnected: true } || !Info.GetEffective<bool>(nameof(ConnectionInfo.RedirectClipboard)))
        {
            AppLog.Trace($"{Info.Name}: Mac clipboard not pushed ({(Session is { IsConnected: true } ? "clipboard redirection is off" : "not connected")})");
            return;
        }
        var clip = TopLevel.GetTopLevel(View)?.Clipboard;
        if (clip is null) return;

        // files take precedence: Finder puts both file URLs and a text form on the pasteboard
        var files = await clip.TryGetFilesAsync().ConfigureAwait(true);
        // APFS keeps names as typed (often NFD); Windows expects composed characters, and APFS lookups are normalisation-insensitive
        var paths = files?.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!.Normalize(System.Text.NormalizationForm.FormC)).ToList();
        var itemCount = files?.Length ?? 0;
        AppLog.Trace($"{Info.Name}: Mac clipboard holds {itemCount} file item(s), {paths?.Count ?? 0} with a local path{(paths is { Count: > 0 } ? ": " + string.Join(" | ", paths) : "")}");
        if (paths is { Count: > 0 })
        {
            var key = "files:" + string.Join('\n', paths);
            if (key == _lastLocalClipboard) { AppLog.Trace($"{Info.Name}: same files as last time, not announced again"); return; }
            _lastLocalClipboard = key;
            foreach (var p in paths.Where(p => !File.Exists(p) && !Directory.Exists(p)))
                Notifications.Instance.Warn($"{Info.Name}: Clipboard: {p} does not exist or is not readable");
            var sentFiles = Session.SetClipboardFiles(paths);
            Notifications.Instance.Info($"{Info.Name}: Clipboard: {paths.Count} file(s) from the Mac {(sentFiles ? "announced to the session; paste there to copy them" : "kept until the channel is ready")}");
            return;
        }

        var text = await clip.TryGetTextAsync().ConfigureAwait(true);
        if (string.IsNullOrEmpty(text)) { AppLog.Trace($"{Info.Name}: Mac clipboard has no text or files"); return; }
        if (text == _lastLocalClipboard) { AppLog.Trace($"{Info.Name}: same text as last time, not announced again"); return; }
        _lastLocalClipboard = text;
        var sent = Session.SetClipboardText(text);
        Notifications.Instance.Info($"{Info.Name}: Clipboard: {text.Length} characters from the Mac {(sent ? "announced to the session" : "kept until the channel is ready")}");
    }

    // ----- files from the session -----

    private int _fetchCounter;
    private bool _fetchPromised; // the running fetch put file promises on the pasteboard (Finder greys out Paste until the real URLs replace them)

    private void OnClipboardFilesOffered(int count, int folders, ulong totalBytes, string preview)
    {
        var mb = totalBytes / (1024.0 * 1024.0);
        var what = $"{count - folders} file(s){(folders > 0 ? $" and {folders} folder(s)" : "")} ({mb:0.#} MB): {preview}";
        if (mb <= _settings.ClipboardFilesAutoFetchMB)
        {
            StartFetch(what, totalBytes);
            return;
        }
        AsyncHelper.Fire(async () =>
        {
            var r = await Dialogs.Dialogs.Message(_owner, "Fetch files from the session?",
                $"{Info.Name} copied {what}. Fetch them to this Mac now? They are put on the clipboard when done.", Dialogs.Dialogs.MsgButtons.YesNo);
            if (r == Dialogs.Dialogs.MsgResult.Yes) StartFetch(what, totalBytes);
            else Notifications.Instance.Info($"{Info.Name}: Clipboard: not fetching {what}");
        }, "Clipboard files");
    }

    private void StartFetch(string what, ulong totalBytes)
    {
        if (Session is null) return;
        var dest = Path.Combine(AppSettings.ClipboardCacheDirectory, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), $"{Info.ConstantId[..8]}-{++_fetchCounter}");
        var started = Session.FetchClipboardFiles(dest, out var token, out var items);
        _fetchPromised = started == RdpSession.FetchStart.StartedWithPromises;
        if (_fetchPromised)
        {
            _lastPasteboardChange = Native.mr_pasteboard_change_count(); // our own promises must not be pushed back
            _lastLocalClipboard = "promise:" + dest;
            _tray.Begin(token, items, totalBytes);
            Notifications.Instance.Info($"{Info.Name}: Clipboard: fetching {what}; drag the card in the session to the Finder, or paste there once ready");
        }
        else if (started == RdpSession.FetchStart.Started)
            Notifications.Instance.Info($"{Info.Name}: Clipboard: fetching {what} into {dest}");
        else
            Notifications.Instance.Warn($"{Info.Name}: Clipboard: could not start fetching {what} (a transfer is already running or the session offered nothing)");
    }

    /// <summary>Test mode: shows the tray for a file that is already on disk, with a simulated 2-second transfer.</summary>
    internal void DemoFileTray(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(path);
        var isDir = Directory.Exists(path);
        var token = Native.mr_promise_publish(dir, name + "\n", isDir ? "d" : "f");
        _lastPasteboardChange = Native.mr_pasteboard_change_count();
        var total = isDir ? 0UL : (ulong)new FileInfo(path).Length;
        _tray.Begin(token, [(isDir, name)], total);
        var step = 0;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => { });
        timer.Tick += (_, _) =>
        {
            step++;
            if (step < 10) { _tray.Progress(total * (ulong)step / 10, total, name); return; }
            timer.Stop();
            Native.mr_promise_complete(token, null);
            _tray.Finish(1, null);
        };
        timer.Start();
    }

    private void OnClipboardFileProgress(ulong done, ulong total, string name)
    {
        var pct = total > 0 ? (int)(done * 100 / total) : 0;
        _tray.Progress(done, total, name);
        Notifications.Instance.Info($"{Info.Name}: Clipboard: {name} … {done / 1048576.0:0.#} of {total / 1048576.0:0.#} MB ({pct}%)");
    }

    private async Task OnClipboardFilesDoneAsync(string dest, int files, string? error)
    {
        _tray.Finish(files, error);
        if (error is not null)
        {
            Notifications.Instance.Error($"{Info.Name}: Clipboard: file transfer failed: {error}");
            return;
        }
        var top = TopLevel.GetTopLevel(View);
        AppLog.Trace($"{Info.Name}: transfer finished, {files} file(s) in {dest} (folder exists: {Directory.Exists(dest)}, window: {top is not null}, promised: {_fetchPromised})");
        if (_fetchPromised && Native.mr_pasteboard_change_count() != _lastPasteboardChange)
        {
            // Finder only honours file promises for drag and drop, so the real URLs must be written now; but the user
            // copied something else meanwhile, and that must not be replaced behind their back
            Notifications.Instance.Warn($"{Info.Name}: Clipboard: {files} file(s) from the session were received, but the Mac clipboard changed meanwhile; they are in {dest}");
            return;
        }
        if (top is null || !Directory.Exists(dest)) { Notifications.Instance.Warn($"{Info.Name}: Clipboard: received files, but {dest} is missing"); return; }
        var transfer = new DataTransfer();
        var names = new List<string>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(dest).OrderBy(e => e, StringComparer.Ordinal))
        {
            IStorageItem? item = Directory.Exists(entry)
                ? await top.StorageProvider.TryGetFolderFromPathAsync(new Uri(entry)).ConfigureAwait(true)
                : await top.StorageProvider.TryGetFileFromPathAsync(new Uri(entry)).ConfigureAwait(true);
            AppLog.Trace($"{Info.Name}: clipboard item {entry} -> {(item is null ? "could not be opened" : item.Path.ToString())}");
            if (item is null) continue;
            transfer.Add(DataTransferItem.CreateFile(item));
            names.Add(Path.GetFileName(entry));
        }
        if (names.Count == 0) { Notifications.Instance.Warn($"{Info.Name}: Clipboard: nothing was received"); return; }
        try
        {
            await top.Clipboard!.SetDataAsync(transfer).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CrashLog.Write("Clipboard files", ex);
            Notifications.Instance.Error($"{Info.Name}: Clipboard: the files were received into {dest} but could not be put on the Mac clipboard: {ex.Message}");
            return;
        }
        _lastLocalClipboard = "files:" + string.Join('\n', Directory.EnumerateFileSystemEntries(dest).OrderBy(e => e, StringComparer.Ordinal));
        _lastPasteboardChange = Native.mr_pasteboard_change_count(); // our own write must not be pushed back
        AppLog.Trace($"{Info.Name}: Mac clipboard now holds the received files (change count {_lastPasteboardChange})");
        Notifications.Instance.Info($"{Info.Name}: Clipboard: {files} file(s) from the session are on the Mac clipboard ({string.Join(", ", names.Take(3))}{(names.Count > 3 ? ", …" : "")}); paste in Finder to copy them");
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
        _clipboardTimer.Stop();
        _idleTimer.Stop();
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
