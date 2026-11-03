using System.Runtime.InteropServices;
using MRemoteMac.Rdp;

namespace MRemoteMac.Services;

/// <summary>
/// Secrets in the macOS login keychain (file-based generic password items), or Windows Credential Manager on Windows.
/// The key-encryption key of the connection database lives here, so unlocking the vault needs the user's login session;
/// the item can be inspected in Keychain Access or Credential Manager. On macOS access to an item is tied to the
/// signature of the app that created it; Windows has no such tie (see <see cref="WindowsCredentials"/>).
/// </summary>
public static class Keychain
{
    public const string Service = "com.pollysec.mremotemac";
    private const int ErrSecItemNotFound = -25300;
    public const int ErrSecUserCanceled = -128;

    public sealed class KeychainException(int status) : Exception($"Keychain error {status}")
    {
        public int Status { get; } = status;
    }

    /// <summary>Returns the stored bytes, or null when no item exists. Throws on any other error (e.g. access denied).</summary>
    public static byte[]? Get(string account)
    {
        if (Platform.IsWindows) return Windows(() => WindowsCredentials.Read(Target(account)));
        var st = Native.mr_keychain_get(Service, account, out var data, out var len);
        if (st == ErrSecItemNotFound) return null;
        if (st != 0) throw new KeychainException(st);
        try
        {
            var bytes = new byte[len];
            Marshal.Copy(data, bytes, 0, len);
            return bytes;
        }
        finally { Native.mr_keychain_free(data); }
    }

    public static void Set(string account, ReadOnlySpan<byte> value, string label)
    {
        if (Platform.IsWindows)
        {
            var copy = value.ToArray();
            try { Windows<object?>(() => { WindowsCredentials.Write(Target(account), copy, label); return null; }); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(copy); }
            return;
        }
        var st = Native.mr_keychain_set(Service, account, value, value.Length, label);
        if (st != 0) throw new KeychainException(st);
    }

    public static void Delete(string account)
    {
        if (Platform.IsWindows)
        {
            Windows<object?>(() => { WindowsCredentials.Delete(Target(account)); return null; });
            return;
        }
        var st = Native.mr_keychain_delete(Service, account);
        if (st != 0 && st != ErrSecItemNotFound) throw new KeychainException(st);
    }

    private static string Target(string account) => $"{Service}/{account}";

    /// <summary>Failures surface as the same exception as on macOS, which the store code already handles.</summary>
    private static T Windows<T>(Func<T> call)
    {
        try { return call(); }
        catch (System.ComponentModel.Win32Exception ex) { throw new KeychainException(ex.NativeErrorCode); }
    }
}
