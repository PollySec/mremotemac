using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MRemoteMac.Config;

/// <summary>
/// The plaintext sidecar next to the connection database (<c>connections.db.vault</c>): which unlock mode is in use,
/// the KDF parameters and the data-encryption key wrapped with AES-256-GCM. The database itself is always encrypted
/// with the random 32-byte data key; changing the master password or switching to the keychain only re-wraps that
/// key, as KDBX4-style vaults do. The salt and parameters are not secret.
/// </summary>
public sealed partial class VaultHeader
{
    public const string Extension = ".vault";
    public const string KeychainMode = "keychain";
    public const string PasswordMode = "password";
    public const int DefaultIterations = 600_000; // OWASP 2025 for PBKDF2-HMAC-SHA256
    private static readonly byte[] Aad = "mRemoteMac vault v1"u8.ToArray();

    public int Version { get; set; } = 1;
    public string Mode { get; set; } = KeychainMode;
    /// <summary>Keychain account that holds the key-encryption key (random id, so the vault survives moves and renames).</summary>
    public string KeychainAccount { get; set; } = "";
    public string Kdf { get; set; } = "PBKDF2-HMAC-SHA256";
    public int Iterations { get; set; } = DefaultIterations;
    public string Salt { get; set; } = "";
    /// <summary>nonce(12) + ciphertext(32) + tag(16), base64.</summary>
    public string WrappedKey { get; set; } = "";

    public static string PathFor(string dbPath) => dbPath + Extension;

    public static VaultHeader? Read(string dbPath)
    {
        var p = PathFor(dbPath);
        if (!File.Exists(p)) return null;
        var h = JsonSerializer.Deserialize(File.ReadAllText(p), VaultJsonContext.Default.VaultHeader) ?? throw new InvalidDataException("Corrupt vault header.");
        if (h.Version != 1) throw new InvalidDataException($"Unsupported vault header version {h.Version}.");
        return h;
    }

    public void Write(string dbPath)
    {
        var p = PathFor(dbPath);
        var tmp = p + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, VaultJsonContext.Default.VaultHeader));
        File.Move(tmp, p, overwrite: true);
    }

    /// <summary>Header for keychain mode: the key-encryption key lives in the keychain, the data key is wrapped here.</summary>
    public static VaultHeader ForKeychain(byte[] kek, byte[] dataKey, string keychainAccount)
        => new() { Mode = KeychainMode, KeychainAccount = keychainAccount, Salt = "", Iterations = 0, Kdf = "none", WrappedKey = Wrap(kek, dataKey) };

    public static string NewKeychainAccount() => "vault:" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    /// <summary>Header for master-password mode: PBKDF2 over a fresh salt wraps the data key.</summary>
    public static VaultHeader ForPassword(string password, byte[] dataKey, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var kek = DeriveKek(password, salt, iterations);
        try { return new VaultHeader { Mode = PasswordMode, Salt = Convert.ToBase64String(salt), Iterations = iterations, WrappedKey = Wrap(kek, dataKey) }; }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    public byte[] UnwrapWithKeychainKey(byte[] kek) => Unwrap(kek);

    /// <summary>Returns the data key, or null when the password is wrong (the GCM tag fails).</summary>
    public byte[]? UnwrapWithPassword(string password)
    {
        var kek = DeriveKek(password, Convert.FromBase64String(Salt), Iterations);
        try { return Unwrap(kek); }
        catch (CryptographicException) { return null; }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    private static byte[] DeriveKek(string password, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    private static string Wrap(byte[] kek, byte[] dataKey)
    {
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var ct = new byte[dataKey.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        using (var gcm = new AesGcm(kek, tag.Length)) gcm.Encrypt(nonce, dataKey, ct, tag, Aad);
        return Convert.ToBase64String([.. nonce, .. ct, .. tag]);
    }

    private byte[] Unwrap(byte[] kek)
    {
        var blob = Convert.FromBase64String(WrappedKey);
        const int nonceLen = 12, tagLen = 16;
        if (blob.Length <= nonceLen + tagLen) throw new InvalidDataException("Corrupt wrapped key.");
        var nonce = blob.AsSpan(0, nonceLen);
        var tag = blob.AsSpan(blob.Length - tagLen);
        var ct = blob.AsSpan(nonceLen, blob.Length - nonceLen - tagLen);
        var key = new byte[ct.Length];
        using var gcm = new AesGcm(kek, tagLen);
        gcm.Decrypt(nonce, ct, tag, key, Aad);
        return key;
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(VaultHeader))]
    private sealed partial class VaultJsonContext : JsonSerializerContext;
}
