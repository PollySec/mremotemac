using System.Security.Cryptography;
using System.Text;
using MRemoteMac.Services;

namespace MRemoteMac.Config;

/// <summary>
/// Opens connection stores by path (".db" = encrypted database, anything else = mRemoteNG XML) and manages the
/// database keys. The database is encrypted with a random data key; <see cref="VaultHeader"/> keeps that key wrapped
/// (AES-256-GCM) by either a random key-encryption key in the login keychain or a key derived from the master
/// password with PBKDF2-HMAC-SHA256 (600 000 rounds). Switching modes only re-wraps.
/// </summary>
public static class ConnectionStores
{
    /// <summary>Asks the user for a password; returns null when cancelled. <c>retry</c> is true after a wrong attempt.</summary>
    public delegate Task<string?> PasswordPrompt(string fileName, bool retry);

    public static bool IsDatabasePath(string path) => SqliteConnectionStore.LooksLikeDatabase(path);

    public static string DefaultDatabasePath => Path.Combine(AppSettings.Directory, SqliteConnectionStore.DefaultFileName);

    /// <summary>
    /// Where key-encryption keys live, addressed by the account name recorded in the vault header. The login keychain
    /// by default; the scripted audit (test mode with an isolated MRM_HOME profile) uses a key file next to the
    /// database instead, because keychain ACL prompts would block an unattended run.
    /// </summary>
    public interface IKeyStore
    {
        /// <summary>Returns a fresh buffer the caller owns (and zeroes after use), or null when there is no item.</summary>
        byte[]? Read(string dbPath, string account);
        /// <summary>Stores a copy of <paramref name="key"/>; the caller zeroes its buffer afterwards.</summary>
        void Store(string dbPath, string account, byte[] key, string label);
        void Remove(string dbPath, string account);
    }

    private sealed class KeychainKeyStore : IKeyStore
    {
        public byte[]? Read(string dbPath, string account) => Keychain.Get(account);
        public void Store(string dbPath, string account, byte[] key, string label) => Keychain.Set(account, key, label);
        public void Remove(string dbPath, string account) => Keychain.Delete(account);
    }

    private sealed class FileKeyStore : IKeyStore
    {
        private static string KeyPath(string dbPath) => dbPath + ".key";
        public byte[]? Read(string dbPath, string account) => File.Exists(KeyPath(dbPath)) ? Convert.FromHexString(File.ReadAllText(KeyPath(dbPath)).Trim()) : null;
        public void Store(string dbPath, string account, byte[] key, string label) => File.WriteAllText(KeyPath(dbPath), Convert.ToHexString(key));
        public void Remove(string dbPath, string account) => File.Delete(KeyPath(dbPath));
    }

    /// <summary>The key file is only used by the audit: test mode *and* an isolated profile, never the user's real data.</summary>
    private static bool UseKeyFile => TestMode.IsActive && Environment.GetEnvironmentVariable("MRM_HOME") is { Length: > 0 };

    public static IKeyStore Keys { get; set; } = UseKeyFile ? new FileKeyStore() : new KeychainKeyStore();

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
            var account = VaultHeader.NewKeychainAccount();
            Keys.Store(path, account, kek, $"mRemoteMac connections ({name})");
            try { VaultHeader.ForKeychain(kek, dataKey, account).Write(path); }
            catch (IOException) { Keys.Remove(path, account); throw; }
            finally { CryptographicOperations.ZeroMemory(kek); }
            return SqliteConnectionStore.Open(path, dataKey);
        }

        if (header.Mode == VaultHeader.KeychainMode)
        {
            var kek = Keys.Read(path, header.KeychainAccount) ?? throw new InvalidOperationException(
                $"The key that unlocks {name} is not in this Mac's login keychain (item {header.KeychainAccount}). " +
                "A vault created on another Mac, or whose keychain item was deleted, can only be opened after a master password was set on it.");
            try
            {
                var dataKey = header.UnwrapWithKeychainKey(kek);
                return SqliteConnectionStore.Open(path, dataKey);
            }
            catch (CryptographicException)
            {
                throw new InvalidDataException($"The keychain key does not match {name}'s vault header ({Path.GetFileName(VaultHeader.PathFor(path))} may belong to another copy).");
            }
            finally { CryptographicOperations.ZeroMemory(kek); }
        }

        var wrong = false;
        while (true)
        {
            var password = await askPassword(name, wrong).ConfigureAwait(true);
            if (password is null) return null;
            if (password.Length == 0) continue;
            var dataKey = await Task.Run(() => header.UnwrapWithPassword(password)).ConfigureAwait(true); // 600k PBKDF2 rounds off the UI thread
            if (dataKey is null) { wrong = true; continue; }
            var store = SqliteConnectionStore.Open(path, dataKey);
            store.HasMasterPassword = true;
            return store;
        }
    }

    /// <summary>
    /// Switches a database to a master password (non-empty) or back to a keychain key (null/empty). Only the wrapped
    /// data key changes; the database is not rewritten. The old keychain item is removed only after the new header
    /// is safely on disk, so a failure half-way leaves the previous mode intact.
    /// </summary>
    public static async Task SetMasterPasswordAsync(SqliteConnectionStore store, string? password)
    {
        var previous = VaultHeader.Read(store.Location);
        if (string.IsNullOrEmpty(password))
        {
            var kek = RandomNumberGenerator.GetBytes(32);
            var account = VaultHeader.NewKeychainAccount();
            Keys.Store(store.Location, account, kek, $"mRemoteMac connections ({store.DisplayName})");
            try { VaultHeader.ForKeychain(kek, store.DataKey, account).Write(store.Location); }
            catch (IOException) { Keys.Remove(store.Location, account); throw; }
            finally { CryptographicOperations.ZeroMemory(kek); }
            store.HasMasterPassword = false;
        }
        else
        {
            var header = await Task.Run(() => VaultHeader.ForPassword(password, store.DataKey)).ConfigureAwait(true); // 600k rounds off the UI thread
            header.Write(store.Location);
            store.HasMasterPassword = true;
        }
        if (previous is { Mode: VaultHeader.KeychainMode, KeychainAccount.Length: > 0 }) Keys.Remove(store.Location, previous.KeychainAccount);
    }
}
