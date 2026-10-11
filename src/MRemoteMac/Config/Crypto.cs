using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace MRemoteMac.Config;

public class EncryptionException : Exception
{
    public EncryptionException(string message, Exception? inner = null) : base(message, inner) { }
}

public interface ICryptographyProvider
{
    string Encrypt(string plainText, string password);
    string Decrypt(string cipherText, string password);
}

/// <summary>
/// Bit-compatible with mRemoteNG's AeadCryptographyProvider (ConfVersion >= 2.6):
/// PBKDF2-HMAC-SHA1 (password chars as low bytes), 16-byte salt, 16-byte nonce (GCM/EAX; 11 bytes for CCM),
/// 128-bit tag, salt used as associated data. Output = base64(salt || nonce || ciphertext || tag).
/// </summary>
public sealed class AeadCryptographyProvider : ICryptographyProvider
{
    public string Engine { get; }
    public string Mode { get; }
    public int KdfIterations { get; }

    private const int SaltBytes = 16;
    private const int MacBits = 128;
    private const int KeyBits = 256;

    public AeadCryptographyProvider(string engine = "AES", string mode = "GCM", int kdfIterations = 10000)
    {
        Engine = engine;
        Mode = mode;
        KdfIterations = Math.Max(1000, kdfIterations);
    }

    private int NonceBytes => Mode.Equals("CCM", StringComparison.OrdinalIgnoreCase) ? 11 : 16;

    private IAeadBlockCipher CreateCipher()
    {
        IBlockCipher engine = Engine.ToUpperInvariant() switch
        {
            "AES" => new AesEngine(),
            "TWOFISH" => new TwofishEngine(),
            "SERPENT" => new SerpentEngine(),
            _ => throw new EncryptionException($"Unsupported encryption engine {Engine}")
        };
        return Mode.ToUpperInvariant() switch
        {
            "GCM" => new GcmBlockCipher(engine),
            "CCM" => new CcmBlockCipher(engine),
            "EAX" => new EaxBlockCipher(engine),
            _ => throw new EncryptionException($"Unsupported block cipher mode {Mode}")
        };
    }

    public static byte[] DeriveKey(string password, byte[] salt, int iterations)
    {
        // BouncyCastle Pkcs5PasswordToBytes: each char -> low byte
        var pw = new byte[password.Length];
        for (var i = 0; i < password.Length; i++) pw[i] = (byte)password[i];
        return Rfc2898DeriveBytes.Pbkdf2(pw, salt, iterations, HashAlgorithmName.SHA1, KeyBits / 8);
    }

    public string Encrypt(string plainText, string password)
    {
        if (string.IsNullOrEmpty(plainText)) return "";
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var key = DeriveKey(password, salt, KdfIterations);
        var cipher = CreateCipher();
        cipher.Init(true, new AeadParameters(new KeyParameter(key), MacBits, nonce, salt));
        var input = Encoding.UTF8.GetBytes(plainText);
        var output = new byte[cipher.GetOutputSize(input.Length)];
        var len = cipher.ProcessBytes(input, 0, input.Length, output, 0);
        len += cipher.DoFinal(output, len);
        var result = new byte[salt.Length + nonce.Length + len];
        Buffer.BlockCopy(salt, 0, result, 0, salt.Length);
        Buffer.BlockCopy(nonce, 0, result, salt.Length, nonce.Length);
        Buffer.BlockCopy(output, 0, result, salt.Length + nonce.Length, len);
        return Convert.ToBase64String(result);
    }

    public string Decrypt(string cipherText, string password)
    {
        if (string.IsNullOrWhiteSpace(cipherText)) return "";
        byte[] data;
        try { data = Convert.FromBase64String(cipherText); }
        catch (FormatException e) { throw new EncryptionException("Cipher text is not valid base64", e); }
        var nonceBytes = NonceBytes;
        if (data.Length < SaltBytes + nonceBytes + MacBits / 8)
            throw new EncryptionException("Cipher text too short");
        var salt = data[..SaltBytes];
        var nonce = data[SaltBytes..(SaltBytes + nonceBytes)];
        var key = DeriveKey(password, salt, KdfIterations);
        var cipher = CreateCipher();
        cipher.Init(false, new AeadParameters(new KeyParameter(key), MacBits, nonce, salt));
        var offset = SaltBytes + nonceBytes;
        var output = new byte[cipher.GetOutputSize(data.Length - offset)];
        try
        {
            var len = cipher.ProcessBytes(data, offset, data.Length - offset, output, 0);
            len += cipher.DoFinal(output, len);
            return Encoding.UTF8.GetString(output, 0, len);
        }
        catch (InvalidCipherTextException e)
        {
            throw new EncryptionException("Decryption failed (wrong password or corrupt data)", e);
        }
    }
}

/// <summary>mRemoteNG legacy provider (ConfVersion &lt; 2.6): AES-128-CBC, key = MD5(UTF8(password)), base64(IV || ct).</summary>
public sealed class LegacyRijndaelCryptographyProvider : ICryptographyProvider
{
    public string Encrypt(string plainText, string password)
    {
        if (string.IsNullOrEmpty(plainText)) return "";
        using var aes = Aes.Create();
        aes.Key = MD5.HashData(Encoding.UTF8.GetBytes(password));
        aes.GenerateIV();
        var ct = aes.EncryptCbc(Encoding.UTF8.GetBytes(plainText), aes.IV, PaddingMode.PKCS7);
        var result = new byte[aes.IV.Length + ct.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(ct, 0, result, aes.IV.Length, ct.Length);
        return Convert.ToBase64String(result);
    }

    public string Decrypt(string cipherText, string password)
    {
        if (string.IsNullOrWhiteSpace(cipherText)) return "";
        try
        {
            var data = Convert.FromBase64String(cipherText);
            using var aes = Aes.Create();
            aes.Key = MD5.HashData(Encoding.UTF8.GetBytes(password));
            var iv = data[..16];
            var pt = aes.DecryptCbc(data.AsSpan(16), iv, PaddingMode.PKCS7);
            return Encoding.UTF8.GetString(pt);
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            throw new EncryptionException("Legacy decryption failed", e);
        }
    }
}
