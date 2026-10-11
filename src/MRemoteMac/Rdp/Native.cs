using System.Runtime.InteropServices;

namespace MRemoteMac.Rdp;

/// <summary>P/Invoke surface of native/libmrrdp.dylib (see native/mrrdp.h). Source-generated marshalling, UTF-8 strings.</summary>
internal static unsafe partial class Native
{
    private const string Lib = "libmrrdp";

    [StructLayout(LayoutKind.Sequential)]
    public struct Callbacks
    {
        public IntPtr user;
        public IntPtr on_frame;
        public IntPtr on_resize;
        public IntPtr on_connected;
        public IntPtr on_disconnected;
        public IntPtr on_verify_cert;
        public IntPtr on_authenticate;
        public IntPtr on_pointer_new;
        public IntPtr on_pointer_free;
        public IntPtr on_pointer_set;
        public IntPtr on_pointer_hidden;
        public IntPtr on_clipboard_text;
    }

    [LibraryImport(Lib)] public static partial IntPtr mr_version();
    [LibraryImport(Lib)] public static partial IntPtr mr_session_create(ref Callbacks callbacks);
    [LibraryImport(Lib)] public static partial void mr_session_destroy(IntPtr s);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int mr_set_string(IntPtr s, string key, string? value);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int mr_set_uint32(IntPtr s, string key, uint value);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int mr_set_bool(IntPtr s, string key, int value);
    [LibraryImport(Lib)] public static partial int mr_parse_args(IntPtr s, int argc, IntPtr* argv);
    [LibraryImport(Lib)] public static partial int mr_connect(IntPtr s);
    [LibraryImport(Lib)] public static partial void mr_disconnect(IntPtr s);
    [LibraryImport(Lib)] public static partial int mr_is_connected(IntPtr s);
    [LibraryImport(Lib)] public static partial int mr_get_framebuffer(IntPtr s, out byte* data, out int width, out int height, out int stride);
    [LibraryImport(Lib)] public static partial void mr_send_mouse(IntPtr s, ushort flags, ushort x, ushort y);
    [LibraryImport(Lib)] public static partial void mr_send_mouse_ext(IntPtr s, ushort flags, ushort x, ushort y);
    [LibraryImport(Lib)] public static partial void mr_send_scancode(IntPtr s, int down, int repeat, uint rdpScancode);
    [LibraryImport(Lib)] public static partial void mr_send_unicode(IntPtr s, int down, ushort code);
    [LibraryImport(Lib)] public static partial void mr_send_sync(IntPtr s, uint toggleFlags);
    [LibraryImport(Lib)] public static partial void mr_send_focus_in(IntPtr s, ushort toggleFlags);
    [LibraryImport(Lib)] public static partial int mr_resize_desktop(IntPtr s, uint width, uint height);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int mr_set_clipboard_text(IntPtr s, string? utf8);

    public static string Version()
    {
        try { return Marshal.PtrToStringUTF8(mr_version()) ?? "?"; }
        catch (DllNotFoundException) { return "libmrrdp not found"; }
    }

    public static int ParseArgs(IntPtr s, IReadOnlyList<string> args)
    {
        var ptrs = new IntPtr[args.Count];
        try
        {
            for (var i = 0; i < args.Count; i++) ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            fixed (IntPtr* p = ptrs)
                return mr_parse_args(s, args.Count, p);
        }
        finally
        {
            foreach (var p in ptrs) if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
        }
    }
}

/// <summary>Constants from FreeRDP input.h.</summary>
internal static class RdpInput
{
    public const ushort PTR_FLAGS_HWHEEL = 0x0400;
    public const ushort PTR_FLAGS_WHEEL = 0x0200;
    public const ushort PTR_FLAGS_WHEEL_NEGATIVE = 0x0100;
    public const ushort PTR_FLAGS_MOVE = 0x0800;
    public const ushort PTR_FLAGS_DOWN = 0x8000;
    public const ushort PTR_FLAGS_BUTTON1 = 0x1000;
    public const ushort PTR_FLAGS_BUTTON2 = 0x2000;
    public const ushort PTR_FLAGS_BUTTON3 = 0x4000;

    public const ushort PTR_XFLAGS_DOWN = 0x8000;
    public const ushort PTR_XFLAGS_BUTTON1 = 0x0001;
    public const ushort PTR_XFLAGS_BUTTON2 = 0x0002;

    public const uint KBD_SYNC_SCROLL_LOCK = 0x01;
    public const uint KBD_SYNC_NUM_LOCK = 0x02;
    public const uint KBD_SYNC_CAPS_LOCK = 0x04;

    public const uint Extended = 0x100;
}
