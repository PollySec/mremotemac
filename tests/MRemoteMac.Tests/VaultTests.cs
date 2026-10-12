using MRemoteMac.Config;
using MRemoteMac.Models;
using Xunit;

namespace MRemoteMac.Tests;

/// <summary>Vault header: key wrapping, unlock modes and the opener's end-to-end behaviour with a file key store.</summary>
public sealed class VaultTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mrm-vault-" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "connections.db");

    private sealed class MemoryKeyStore : ConnectionStores.IKeyStore
    {
        public readonly Dictionary<string, byte[]> Items = new(StringComparer.Ordinal);
        public byte[]? Read(string path) => Items.GetValueOrDefault(path);
        public void Store(string path, byte[] key, string label) => Items[path] = (byte[])key.Clone();
        public void Remove(string path) => Items.Remove(path);
    }

    static VaultTests() => SQLitePCL.Batteries_V2.Init();
    public VaultTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void PasswordModeWrapsAndRejectsWrongPassword()
    {
        var dataKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var h = VaultHeader.ForPassword("correct horse battery staple", dataKey, iterations: 1000);
        Assert.Equal(VaultHeader.PasswordMode, h.Mode);
        Assert.Equal(dataKey, h.UnwrapWithPassword("correct horse battery staple"));
        Assert.Null(h.UnwrapWithPassword("wrong"));
        h.Write(DbPath);
        var back = VaultHeader.Read(DbPath)!;
        Assert.Equal(dataKey, back.UnwrapWithPassword("correct horse battery staple"));
    }

    [Fact]
    public void KeychainModeWrapsWithTheKeyEncryptionKey()
    {
        var dataKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var kek = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var h = VaultHeader.ForKeychain(kek, dataKey);
        Assert.Equal(dataKey, h.UnwrapWithKeychainKey(kek));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => h.UnwrapWithKeychainKey(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
    }

    [Fact]
    public async Task OpenerCreatesUnlocksAndSwitchesModes()
    {
        var keys = new MemoryKeyStore();
        var previous = ConnectionStores.Keys;
        ConnectionStores.Keys = keys;
        try
        {
            var prompts = 0;
            Task<string?> NoPrompt(string _, bool __) { prompts++; return Task.FromResult<string?>(null); }

            using (var store = (SqliteConnectionStore)(await ConnectionStores.OpenAsync(DbPath, NoPrompt))!)
            {
                Assert.False(store.HasMasterPassword);
                var root = new RootNodeInfo(); root.AddChild(new ConnectionInfo { Name = "A", Hostname = "a" });
                store.Save(root);
                ConnectionStores.SetMasterPassword(store, "hunter2");
                Assert.True(store.HasMasterPassword);
            }
            Assert.Empty(keys.Items); // keychain key removed in password mode
            Assert.Equal(VaultHeader.PasswordMode, VaultHeader.Read(DbPath)!.Mode);

            Assert.Null(await ConnectionStores.OpenAsync(DbPath, NoPrompt)); // cancelled prompt
            Assert.Equal(1, prompts);

            var attempts = new Queue<string?>(["wrong", "hunter2"]);
            using (var store = (SqliteConnectionStore)(await ConnectionStores.OpenAsync(DbPath, (_, _) => Task.FromResult(attempts.Dequeue())))!)
            {
                Assert.True(store.HasMasterPassword);
                Assert.Equal("a", store.Load().Descendants().Single().Hostname);
                ConnectionStores.SetMasterPassword(store, null); // back to keychain
                Assert.False(store.HasMasterPassword);
            }
            Assert.Single(keys.Items);
            using var again = (SqliteConnectionStore)(await ConnectionStores.OpenAsync(DbPath, NoPrompt))!;
            Assert.Equal("a", again.Load().Descendants().Single().Hostname);
        }
        finally { ConnectionStores.Keys = previous; }
    }
}
