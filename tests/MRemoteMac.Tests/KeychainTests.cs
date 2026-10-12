using MRemoteMac.Services;
using Xunit;

namespace MRemoteMac.Tests;

/// <summary>Round trip against the real login keychain (generic password item owned by the test host).</summary>
public class KeychainTests
{
    [Fact]
    public void SetGetDeleteRoundTrip()
    {
        var account = "test:" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.Null(Keychain.Get(account));
            var key = new byte[32]; Random.Shared.NextBytes(key);
            Keychain.Set(account, key, "mRemoteMac test item");
            Assert.Equal(key, Keychain.Get(account));
            var key2 = new byte[32]; Random.Shared.NextBytes(key2);
            Keychain.Set(account, key2, "mRemoteMac test item"); // replaces
            Assert.Equal(key2, Keychain.Get(account));
        }
        finally { Keychain.Delete(account); }
        Assert.Null(Keychain.Get(account));
    }
}
