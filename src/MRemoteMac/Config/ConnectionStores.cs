using System.Security.Cryptography;
using System.Text;
using MRemoteMac.Services;

namespace MRemoteMac.Config;

/// <summary>
/// Opens connection stores by path (".db" = encrypted database, anything else = mRemoteNG XML) and manages the
/// database keys: a random 32-byte key per database kept in the login keychain, or a user master password that
/// SQLCipher runs through PBKDF2-HMAC-SHA512 (256 000 rounds, per-file salt in the header).
/// </summary>
public static class ConnectionStores
{
    /// <summary>Asks the user for a password; returns null when cancelled. <c>retry</c> is true after a wrong attempt.</summary>
    public delegate Task<string?> PasswordPrompt(string fileName, bool retry);

    public static bool IsDatabasePath(string path) => SqliteConnectionStore.LooksLikeDatabase(path);

    public static string DefaultDatabasePath => Path.Combine(AppSettings.Directory, SqliteConnectionStore.DefaultFileName);

    /// <summary>
    /// Where database keys live. The login keychain by default; the scripted UI audit uses a key file next to the
    /// database instead, because keychain ACL prompts would block an unattended run.
    /// </summary>
    public interface IKeyStore
    {
        byte[]? Read(string path);
        /// <summary>Stores a copy of <paramref name="key"/>; the caller zeroes its buffer afterwards.</summary>
        void Store(string path, byte[] key, string label);
        void Remove(string path);
    }

    private sealed class KeychainKeyStore : IKeyStore
    {
        public byte[]? Read(string path) => Keychain.Get(KeychainAccount(path));
        public void Store(string path, byte[] key, string label) => Keychain.Set(KeychainAccount(path), key, label);
        public void Remove(string path) => Keychain.Delete(KeychainAccount(path));
    }

    private sealed class FileKeyStore : IKeyStore
    {
        private static string KeyPath(string path) => path + ".key";
        public byte[]? Read(string path) => File.Exists(KeyPath(path)) ? Convert.FromHexString(File.ReadAllText(KeyPath(path)).Trim()) : null;
        public void Store(string path, byte[] key, string label) => File.WriteAllText(KeyPath(path), Convert.ToHexString(key));
        public void Remove(string path) => File.Delete(KeyPath(path));
    }

    public static IKeyStore Keys { get; set; } = TestMode.IsActive ? new FileKeyStore() : new KeychainKeyStore();

    /// <summary>Keychain account name for a database file (one key per file, bound to its path).</summary>
    public static string KeychainAccount(string path)
        => "vault:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))))[..16].ToLowerInvariant();

    /// <summary>
    /// Opens or creates the store at <paramref name="path"/>. For a database: the keychain key is tried first, then
    /// the user is asked for a master password; a new database gets a fresh keychain key. For XML: protected files
    /// prompt for their master password. Returns null when the user cancels.
    /// </summary>
    public static async Task<IConnectionStore?> OpenAsync(string path, PasswordPrompt askPassword)
    {
        if (!IsDatabasePath(path))
        {
            string? password = null;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var store = new XmlConnectionStore(path, password);
                    store.Load();
                    return store;
                }
                catch (ConnectionsFile.PasswordRequiredException)
                {
                    password = await askPassword(Path.GetFileName(path), attempt > 0).ConfigureAwait(true);
                    if (password is null) return null;
                }
            }
        }

        return await OpenDatabaseAsync(path, askPassword).ConfigureAwait(true);
    }

    private static async Task<IConnectionStore?> OpenDatabaseAsync(string path, PasswordPrompt askPassword)
    {
        var name = Path.GetFileName(path);
        var header = File.Exists(path) ? VaultHeader.Read(path) : null; // a header without a database is a leftover
        if (header is null)
        {
            if (File.Exists(path))
                throw new InvalidDataException($"{name} has no vault header ({Path.GetFileName(VaultHeader.PathFor(path))}); it cannot be unlocked.");
            // brand-new vault: random data key, wrapped with a random keychain key
            var dataKey = RandomNumberGenerator.GetBytes(32);
            var kek = RandomNumberGenerator.GetBytes(32);
            Keys.Store(path, kek, $"mRemoteMac connections ({name})");
            VaultHeader.ForKeychain(kek, dataKey).Write(path);
            CryptographicOperations.ZeroMemory(kek);
            return SqliteConnectionStore.Open(path, dataKey);
        }

        if (header.Mode == VaultHeader.KeychainMode)
        {
            var kek = Keys.Read(path) ?? throw new InvalidOperationException(
                $"The key for {name} is not in this Mac's keychain. Open the database on the Mac that created it, or set a master password there first.");
            try
            {
                var dataKey = header.UnwrapWithKeychainKey(kek);
                return SqliteConnectionStore.Open(path, dataKey);
            }
            catch (CryptographicException)
            {
                throw new InvalidDataException($"The keychain key does not match {name}'s vault header.");
            }
            finally { CryptographicOperations.ZeroMemory(kek); }
        }

        for (var attempt = 0; ; attempt++)
        {
            var password = await askPassword(name, attempt > 0).ConfigureAwait(true);
            if (password is null) return null;
            if (password.Length == 0) continue;
            var dataKey = header.UnwrapWithPassword(password);
            if (dataKey is null) continue;
            var store = SqliteConnectionStore.Open(path, dataKey);
            store.HasMasterPassword = true;
            return store;
        }
    }

    /// <summary>
    /// Switches a database to a master password (non-empty) or back to a keychain key (null/empty). Only the wrapped
    /// data key changes; the database is not rewritten.
    /// </summary>
    public static void SetMasterPassword(SqliteConnectionStore store, string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            var kek = RandomNumberGenerator.GetBytes(32);
            Keys.Store(store.Location, kek, $"mRemoteMac connections ({store.DisplayName})");
            VaultHeader.ForKeychain(kek, store.DataKey).Write(store.Location);
            CryptographicOperations.ZeroMemory(kek);
            store.HasMasterPassword = false;
        }
        else
        {
            VaultHeader.ForPassword(password, store.DataKey).Write(store.Location);
            Keys.Remove(store.Location);
            store.HasMasterPassword = true;
        }
    }
}
