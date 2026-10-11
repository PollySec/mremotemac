using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
    public int Width { get; init; }
    public int Height { get; init; }
    public int HotX { get; init; }
    public int HotY { get; init; }
    public byte[] Bgra { get; init; } = Array.Empty<byte>();
    public object? Cached { get; set; } // Avalonia Cursor created lazily on the UI thread
}

/// <summary>
/// A managed RDP session. Owns a native mr_session and a managed BGRA back buffer that mirrors the remote desktop.
/// Events marked (rdp thread) are raised on the FreeRDP thread; the view marshals them to the UI thread.
/// </summary>
public sealed unsafe class RdpSession : IDisposable
{
    private IntPtr _handle;
    private GCHandle _self;
    private bool _disposed;

    public readonly object BufferLock = new();
    public byte[] Buffer { get; private set; } = Array.Empty<byte>();
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Stride => Width * 4;

    // dirty rect accumulated under BufferLock; consumer resets it
    public int DirtyX0 = int.MaxValue, DirtyY0 = int.MaxValue, DirtyX1 = -1, DirtyY1 = -1;

    public bool IsConnected => _handle != IntPtr.Zero && Native.mr_is_connected(_handle) != 0;
    public bool HasConnected { get; private set; }
    public uint LastError { get; private set; }
    public string LastErrorText { get; private set; } = "";

    public event Action? FrameDirty;              // (rdp thread)
    public event Action<int, int>? Resized;       // (rdp thread)
    public event Action? Connected;               // (rdp thread)
    public event Action<uint, string>? Disconnected; // (rdp thread)
    public event Action<RemotePointer?>? PointerChanged; // (rdp thread) null => default cursor
    public event Action? PointerHidden;           // (rdp thread)
    public event Action<string>? ClipboardTextReceived; // (rdp thread)

    /// <summary>Called on the UI thread; must return a decision. Default: accept once.</summary>
    public Func<CertificateInfo, Task<CertificateDecision>>? VerifyCertificate { get; set; }
    /// <summary>Called on the UI thread; return null to cancel.</summary>
    public Func<AuthRequest, Task<Credentials?>>? Authenticate { get; set; }

    public RdpSession()
    {
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
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("mr_session_create failed");
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

    public void Connect()
    {
        var rc = Native.mr_connect(_handle);
        if (rc != 0) throw new InvalidOperationException($"mr_connect failed ({rc})");
    }

    public void Disconnect()
    {
        if (_handle != IntPtr.Zero) Native.mr_disconnect(_handle);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            Native.mr_session_destroy(_handle);
            _handle = IntPtr.Zero;
        }
        if (_self.IsAllocated) _self.Free();
    }

    // ---------------- input ----------------

    public void SendMouse(ushort flags, int x, int y)
    {
        if (_handle == IntPtr.Zero) return;
        Native.mr_send_mouse(_handle, flags, (ushort)Math.Clamp(x, 0, 65535), (ushort)Math.Clamp(y, 0, 65535));
    }

    public void SendMouseExt(ushort flags, int x, int y)
    {
        if (_handle == IntPtr.Zero) return;
        Native.mr_send_mouse_ext(_handle, flags, (ushort)Math.Clamp(x, 0, 65535), (ushort)Math.Clamp(y, 0, 65535));
    }

    public void SendScancode(uint scancode, bool down, bool repeat = false)
    {
        if (_handle != IntPtr.Zero) Native.mr_send_scancode(_handle, down ? 1 : 0, repeat ? 1 : 0, scancode);
    }

    public void SendUnicode(char c, bool down)
    {
        if (_handle != IntPtr.Zero) Native.mr_send_unicode(_handle, down ? 1 : 0, c);
    }

    public void SendSync(uint toggles) { if (_handle != IntPtr.Zero) Native.mr_send_sync(_handle, toggles); }
    public void SendFocusIn(ushort toggles) { if (_handle != IntPtr.Zero) Native.mr_send_focus_in(_handle, toggles); }

    public bool ResizeDesktop(int width, int height)
        => _handle != IntPtr.Zero && Native.mr_resize_desktop(_handle, (uint)width, (uint)height) != 0;

    public bool SetClipboardText(string? text)
        => _handle != IntPtr.Zero && Native.mr_set_clipboard_text(_handle, text) != 0;

    /// <summary>Sends a key combination (press all, release in reverse).</summary>
    public void SendKeyCombination(params uint[] scancodes)
    {
        foreach (var s in scancodes) SendScancode(s, true);
        for (var i = scancodes.Length - 1; i >= 0; i--) SendScancode(scancodes[i], false);
    }

    // ---------------- native callbacks (rdp thread) ----------------

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnFrame(IntPtr user, int x, int y, int w, int h)
    {
        var s = From(user);
        if (s is null || s._handle == IntPtr.Zero) return;
        if (Native.mr_get_framebuffer(s._handle, out var src, out var fbw, out var fbh, out var stride) == 0) return;
        lock (s.BufferLock)
        {
            if (s.Width != fbw || s.Height != fbh) s.Reallocate(fbw, fbh);
            var x1 = Math.Min(x + w, fbw);
            var y1 = Math.Min(y + h, fbh);
            if (x < 0) x = 0;
            if (y < 0) y = 0;
            if (x1 <= x || y1 <= y) return;
            var rowBytes = (x1 - x) * 4;
            fixed (byte* dst = s.Buffer)
            {
                for (var row = y; row < y1; row++)
                    System.Buffer.MemoryCopy(src + row * stride + x * 4, dst + row * s.Stride + x * 4, rowBytes, rowBytes);
            }
            s.DirtyX0 = Math.Min(s.DirtyX0, x); s.DirtyY0 = Math.Min(s.DirtyY0, y);
            s.DirtyX1 = Math.Max(s.DirtyX1, x1); s.DirtyY1 = Math.Max(s.DirtyY1, y1);
        }
        s.FrameDirty?.Invoke();
    }

    private void Reallocate(int w, int h)
    {
        Width = w; Height = h;
        Buffer = new byte[w * h * 4];
        DirtyX0 = 0; DirtyY0 = 0; DirtyX1 = w; DirtyY1 = h;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnResize(IntPtr user, int w, int h)
    {
        var s = From(user);
        if (s is null) return;
        lock (s.BufferLock)
        {
            s.Reallocate(w, h);
            if (s._handle != IntPtr.Zero && Native.mr_get_framebuffer(s._handle, out var src, out var fbw, out var fbh, out var stride) != 0 && fbw == w && fbh == h)
            {
                fixed (byte* dst = s.Buffer)
                    for (var row = 0; row < h; row++)
                        System.Buffer.MemoryCopy(src + row * stride, dst + row * s.Stride, s.Stride, s.Stride);
            }
        }
        s.Resized?.Invoke(w, h);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnConnected(IntPtr user)
    {
        var s = From(user);
        if (s is null) return;
        s.HasConnected = true;
        s.Connected?.Invoke();
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnDisconnected(IntPtr user, uint error, IntPtr name, IntPtr desc)
    {
        var s = From(user);
        if (s is null) return;
        s.LastError = error;
        var n = Marshal.PtrToStringUTF8(name) ?? "";
        var d = Marshal.PtrToStringUTF8(desc) ?? "";
        s.LastErrorText = error == 0 ? "" : string.IsNullOrEmpty(d) ? n : $"{d} ({n})";
        s.Disconnected?.Invoke(error, s.LastErrorText);
    }

    private static string Str(IntPtr p) => Marshal.PtrToStringUTF8(p) ?? "";

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OnVerifyCert(IntPtr user, IntPtr host, ushort port, IntPtr cn, IntPtr subject, IntPtr issuer,
        IntPtr fingerprint, uint flags, int changed, IntPtr oldSubject, IntPtr oldIssuer, IntPtr oldFingerprint)
    {
        var s = From(user);
        if (s?.VerifyCertificate is null) return (int)CertificateDecision.AcceptOnce;
        var info = new CertificateInfo(Str(host), port, Str(cn), Str(subject), Str(issuer), Str(fingerprint), flags,
            changed != 0, changed != 0 ? Str(oldSubject) : null, changed != 0 ? Str(oldIssuer) : null,
            changed != 0 ? Str(oldFingerprint) : null);
        try
        {
            return (int)Dispatcher.UIThread.InvokeAsync(() => s.VerifyCertificate(info)).GetAwaiter().GetResult();
        }
        catch
        {
            return (int)CertificateDecision.Reject;
        }
    }

    private static void WriteUtf8(byte* dst, int cap, string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var n = Math.Min(bytes.Length, cap - 1);
        for (var i = 0; i < n; i++) dst[i] = bytes[i];
        dst[n] = 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OnAuthenticate(IntPtr user, int reason, byte* u, int ucap, byte* p, int pcap, byte* d, int dcap)
    {
        var s = From(user);
        if (s?.Authenticate is null) return 0;
        var req = new AuthRequest(reason, Str((IntPtr)u), Str((IntPtr)p), Str((IntPtr)d));
        Credentials? result;
        try
        {
            result = Dispatcher.UIThread.InvokeAsync(() => s.Authenticate(req)).GetAwaiter().GetResult();
        }
        catch
        {
            return 0;
        }
        if (result is null) return 0;
        WriteUtf8(u, ucap, result.Username);
        WriteUtf8(p, pcap, result.Password);
        WriteUtf8(d, dcap, result.Domain);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr OnPointerNew(IntPtr user, int w, int h, int hotX, int hotY, byte* bgra)
    {
        var data = new byte[w * h * 4];
        Marshal.Copy((IntPtr)bgra, data, 0, data.Length);
        var ptr = new RemotePointer { Width = w, Height = h, HotX = hotX, HotY = hotY, Bgra = data };
        return GCHandle.ToIntPtr(GCHandle.Alloc(ptr));
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnPointerFree(IntPtr user, IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        var gch = GCHandle.FromIntPtr(handle);
        if (gch.Target is RemotePointer { Cached: IDisposable disp })
            Dispatcher.UIThread.Post(() => disp.Dispose());
        gch.Free();
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnPointerSet(IntPtr user, IntPtr handle)
    {
        var s = From(user);
        if (s is null) return;
        var ptr = handle == IntPtr.Zero ? null : GCHandle.FromIntPtr(handle).Target as RemotePointer;
        s.PointerChanged?.Invoke(ptr);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnPointerHidden(IntPtr user)
    {
        From(user)?.PointerHidden?.Invoke();
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnClipboardText(IntPtr user, IntPtr utf8)
    {
        var s = From(user);
        if (s is null) return;
        s.ClipboardTextReceived?.Invoke(Str(utf8));
    }
}
