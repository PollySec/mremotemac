using System.Runtime.InteropServices;
using MRemoteMac.Rdp;

namespace MRemoteMac.Services;

/// <summary>
/// Secrets in the macOS login keychain (file-based generic password items). The key-encryption key of the connection
/// database lives here, so unlocking the vault needs the user's login session; the item can be inspected in Keychain
/// Access like any other login item. Access to an item is tied to the signature of the app that created it.
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
        var st = Native.mr_keychain_set(Service, account, value, value.Length, label);
        if (st != 0) throw new KeychainException(st);
    }

    public static void Delete(string account)
    {
        var st = Native.mr_keychain_delete(Service, account);
        if (st != 0 && st != ErrSecItemNotFound) throw new KeychainException(st);
    }
}
