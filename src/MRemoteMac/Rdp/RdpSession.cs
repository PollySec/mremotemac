using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Threading;

namespace MRemoteMac.Rdp;

public sealed record CertificateInfo(string Host, int Port, string CommonName, string Subject, string Issuer,
    string Fingerprint, uint Flags, bool Changed, string? OldSubject, string? OldIssuer, string? OldFingerprint);

public enum CertificateDecision { Reject = 0, AcceptPermanently = 1, AcceptOnce = 2 }

public sealed record AuthRequest(int Reason, string Username, string Password, string Domain)
{
    public string ReasonText => Reason switch
    {
        0 => "NLA", 1 => "TLS", 2 => "RDP", 3 => "Gateway (HTTP)", 4 => "Gateway (RDG)", 5 => "Gateway (RPC)",
        6 => "Smartcard PIN", 7 => "RDSTLS", _ => "Login"
    };
}

public sealed record Credentials(string Username, string Password, string Domain);

public sealed class RemotePointer
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int HotX { get; init; }
    public required int HotY { get; init; }
    public required byte[] Bgra { get; init; }
    /// <summary>Platform cursor created lazily on the UI thread.</summary>
    public object? Cached { get; set; }
}

/// <summary>Receives framebuffer updates straight from the FreeRDP thread (no intermediate copy).</summary>
public interface IRdpFrameTarget
{
    /// <summary>The remote desktop changed size; subsequent writes use the new geometry. Called on the RDP thread.</summary>
    void Resize(int width, int height);

    /// <summary>Copies a dirty rectangle of 32bpp BGRA pixels. <paramref name="src"/> points at the top-left pixel of the rect. RDP thread.</summary>
    unsafe void Write(byte* src, int srcStride, int x, int y, int width, int height);

    /// <summary>A batch of writes is complete and may be shown. RDP thread.</summary>
    void Present();
}

/// <summary>
/// A managed RDP session over the native shim. Events documented as "RDP thread" are raised on the FreeRDP
/// worker thread; consumers must marshal to the UI thread themselves. Certificate and credential prompts block
/// the RDP thread while the answer is produced on the UI thread, which is what FreeRDP's synchronous callbacks require.
/// </summary>
public sealed unsafe class RdpSession : IDisposable
{
    private IntPtr _handle;
    private GCHandle _self;
    private int _disposed;
    private int _connected;
    private IRdpFrameTarget? _target;

    public bool IsConnected => Volatile.Read(ref _connected) != 0;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint LastError { get; private set; }
    public string LastErrorText { get; private set; } = "";

    public event Action? Connected;                        // RDP thread
    public event Action<uint, string>? Disconnected;       // RDP thread
    public event Action<RemotePointer?>? PointerChanged;   // RDP thread; null = default cursor
    public event Action? PointerHidden;                    // RDP thread
    public event Action<string>? ClipboardTextReceived;    // RDP thread

    /// <summary>Invoked on the UI thread; default accepts the certificate for this session only.</summary>
    public Func<CertificateInfo, Task<CertificateDecision>>? VerifyCertificate { get; set; }

    /// <summary>Invoked on the UI thread; return null to cancel the connection.</summary>
    public Func<AuthRequest, Task<Credentials?>>? Authenticate { get; set; }

    public RdpSession(IRdpFrameTarget target)
    {
        _target = target;
        _self = GCHandle.Alloc(this);
        var cb = new Native.Callbacks
        {
            user = GCHandle.ToIntPtr(_self),
            on_frame = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, int, int, int, void>)&OnFrame,
            on_resize = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, int, void>)&OnResize,
            on_connected = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&OnConnected,
            on_disconnected = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint, IntPtr, IntPtr, void>)&OnDisconnected,
            on_verify_cert = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, ushort, IntPtr, IntPtr, IntPtr, IntPtr, uint, int, IntPtr, IntPtr, IntPtr, int>)&OnVerifyCert,
            on_authenticate = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int, byte*, int, byte*, int, int>)&OnAuthenticate,
            on_pointer_new = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, int, int, int, byte*, IntPtr>)&OnPointerNew,
            on_pointer_free = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnPointerFree,
            on_pointer_set = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnPointerSet,
            on_pointer_hidden = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&OnPointerHidden,
            on_clipboard_text = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnClipboardText,
        };
        _handle = Native.mr_session_create(ref cb);
        if (_handle == IntPtr.Zero)
        {
            _self.Free();
            throw new InvalidOperationException("mr_session_create failed");
        }
    }

    private static RdpSession? From(IntPtr user) => GCHandle.FromIntPtr(user).Target as RdpSession;

    // ---------------- settings ----------------

    public bool SetString(string key, string? value) => Native.mr_set_string(_handle, key, value) != 0;
    public bool SetUInt32(string key, uint value) => Native.mr_set_uint32(_handle, key, value) != 0;
    public bool SetBool(string key, bool value) => Native.mr_set_bool(_handle, key, value ? 1 : 0) != 0;

    public int ParseArgs(IEnumerable<string> args)
    {
        var list = new List<string> { "mrrdp" };
        list.AddRange(args);
        return Native.ParseArgs(_handle, list);
    }

    // ---------------- lifecycle ----------------

    /// <summary>Starts the connection on the FreeRDP worker thread and returns immediately.</summary>
    public void Connect()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var rc = Native.mr_connect(_handle);
        if (rc != 0) throw new InvalidOperationException($"mr_connect failed ({rc})");
    }

    /// <summary>Aborts the connection and joins the worker thread. Blocking; never call on the UI thread while a prompt may be pending.</summary>
    public void Disconnect()
    {
        if (_handle != IntPtr.Zero) Native.mr_disconnect(_handle);
    }

    public Task DisconnectAsync() => Task.Run(Disconnect);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_handle != IntPtr.Zero)
        {
            Native.mr_session_destroy(_handle); // disconnects and joins first, so no callback can run after this
            _handle = IntPtr.Zero;
        }
        _target = null;
        if (_self.IsAllocated) _self.Free();
    }

    // ---------------- input (any thread) ----------------

    public void SendMouse(ushort flags, int x, int y)
    {
        if (IsConnected) Native.mr_send_mouse(_handle, flags, (ushort)Math.Clamp(x, 0, ushort.MaxValue), (ushort)Math.Clamp(y, 0, ushort.MaxValue));
    }

    public void SendMouseExt(ushort flags, int x, int y)
    {
        if (IsConnected) Native.mr_send_mouse_ext(_handle, flags, (ushort)Math.Clamp(x, 0, ushort.MaxValue), (ushort)Math.Clamp(y, 0, ushort.MaxValue));
    }

    public void SendScancode(uint scancode, bool down, bool repeat = false)
    {
        if (IsConnected) Native.mr_send_scancode(_handle, down ? 1 : 0, repeat ? 1 : 0, scancode);
    }

    public void SendUnicode(char c, bool down)
    {
        if (IsConnected) Native.mr_send_unicode(_handle, down ? 1 : 0, c);
    }

    public void SendSync(uint toggles) { if (IsConnected) Native.mr_send_sync(_handle, toggles); }
    public void SendFocusIn(ushort toggles) { if (IsConnected) Native.mr_send_focus_in(_handle, toggles); }

    public bool ResizeDesktop(int width, int height)
        => IsConnected && Native.mr_resize_desktop(_handle, (uint)width, (uint)height) != 0;

    public bool SetClipboardText(string? text)
        => _handle != IntPtr.Zero && Native.mr_set_clipboard_text(_handle, text) != 0;

    /// <summary>Presses all scancodes in order and releases them in reverse.</summary>
    public void SendKeyCombination(params ReadOnlySpan<uint> scancodes)
    {
        foreach (var s in scancodes) SendScancode(s, true);
        for (var i = scancodes.Length - 1; i >= 0; i--) SendScancode(scancodes[i], false);
    }

    // ---------------- native callbacks (RDP thread) ----------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnFrame(IntPtr user, int x, int y, int w, int h)
    {
        var s = From(user);
        if (s?._target is not { } target || s._handle == IntPtr.Zero) return;
        if (Native.mr_get_framebuffer(s._handle, out var src, out var fbw, out var fbh, out var stride) == 0) return;
        if (s.Width != fbw || s.Height != fbh)
        {
            s.Width = fbw; s.Height = fbh;
            target.Resize(fbw, fbh);
            target.Write(src, stride, 0, 0, fbw, fbh);
            target.Present();
            return;
        }
        var x1 = Math.Min(x + w, fbw);
        var y1 = Math.Min(y + h, fbh);
        x = Math.Max(x, 0); y = Math.Max(y, 0);
        if (x1 <= x || y1 <= y) return;
        target.Write(src + (long)y * stride + x * 4, stride, x, y, x1 - x, y1 - y);
        target.Present();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnResize(IntPtr user, int w, int h)
    {
        var s = From(user);
        if (s?._target is not { } target) return;
        s.Width = w; s.Height = h;
        target.Resize(w, h);
        if (s._handle != IntPtr.Zero && Native.mr_get_framebuffer(s._handle, out var src, out var fbw, out var fbh, out var stride) != 0 && fbw == w && fbh == h)
        {
            target.Write(src, stride, 0, 0, w, h);
            target.Present();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnConnected(IntPtr user)
    {
        var s = From(user);
        if (s is null) return;
        Volatile.Write(ref s._connected, 1);
        s.Connected?.Invoke();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDisconnected(IntPtr user, uint error, IntPtr name, IntPtr desc)
    {
        var s = From(user);
        if (s is null) return;
        Volatile.Write(ref s._connected, 0);
        s.LastError = error;
        var n = Str(name);
        var d = Str(desc);
        s.LastErrorText = error == 0 ? "" : string.IsNullOrEmpty(d) ? n : $"{d} ({n})";
        s.Disconnected?.Invoke(error, s.LastErrorText);
    }

    private static string Str(IntPtr p) => Marshal.PtrToStringUTF8(p) ?? "";

    private static TResult AskUi<TResult>(Func<Task<TResult>> ask, TResult fallback)
    {
        if (Dispatcher.UIThread.CheckAccess()) return fallback; // would deadlock; never expected on the RDP thread
        try { return Dispatcher.UIThread.InvokeAsync(ask).GetAwaiter().GetResult(); }
        catch { return fallback; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnVerifyCert(IntPtr user, IntPtr host, ushort port, IntPtr cn, IntPtr subject, IntPtr issuer,
        IntPtr fingerprint, uint flags, int changed, IntPtr oldSubject, IntPtr oldIssuer, IntPtr oldFingerprint)
    {
        var s = From(user);
        if (s?.VerifyCertificate is not { } verify) return (int)CertificateDecision.AcceptOnce;
        var info = new CertificateInfo(Str(host), port, Str(cn), Str(subject), Str(issuer), Str(fingerprint), flags,
            changed != 0, changed != 0 ? Str(oldSubject) : null, changed != 0 ? Str(oldIssuer) : null,
            changed != 0 ? Str(oldFingerprint) : null);
        return (int)AskUi(() => verify(info), CertificateDecision.Reject);
    }

    private static void WriteUtf8(byte* dst, int cap, string value)
    {
        var span = new Span<byte>(dst, cap);
        var n = Encoding.UTF8.GetBytes(value.AsSpan(), span[..(cap - 1)]);
        span[n] = 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnAuthenticate(IntPtr user, int reason, byte* u, int ucap, byte* p, int pcap, byte* d, int dcap)
    {
        var s = From(user);
        if (s?.Authenticate is not { } authenticate) return 0;
        var req = new AuthRequest(reason, Str((IntPtr)u), Str((IntPtr)p), Str((IntPtr)d));
        var result = AskUi(() => authenticate(req), null);
        if (result is null) return 0;
        WriteUtf8(u, ucap, result.Username);
        WriteUtf8(p, pcap, result.Password);
        WriteUtf8(d, dcap, result.Domain);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IntPtr OnPointerNew(IntPtr user, int w, int h, int hotX, int hotY, byte* bgra)
    {
        var data = new ReadOnlySpan<byte>(bgra, w * h * 4).ToArray();
        var ptr = new RemotePointer { Width = w, Height = h, HotX = hotX, HotY = hotY, Bgra = data };
        return GCHandle.ToIntPtr(GCHandle.Alloc(ptr));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPointerFree(IntPtr user, IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        var gch = GCHandle.FromIntPtr(handle);
        if (gch.Target is RemotePointer { Cached: IDisposable cached })
            Dispatcher.UIThread.Post(cached.Dispose);
        gch.Free();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPointerSet(IntPtr user, IntPtr handle)
    {
        var s = From(user);
        if (s is null) return;
        var ptr = handle == IntPtr.Zero ? null : GCHandle.FromIntPtr(handle).Target as RemotePointer;
        s.PointerChanged?.Invoke(ptr);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPointerHidden(IntPtr user) => From(user)?.PointerHidden?.Invoke();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnClipboardText(IntPtr user, IntPtr utf8) => From(user)?.ClipboardTextReceived?.Invoke(Str(utf8));
}
