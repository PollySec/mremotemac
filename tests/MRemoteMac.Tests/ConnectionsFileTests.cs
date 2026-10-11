using MRemoteMac.Config;
using MRemoteMac.Models;
using Xunit;

namespace MRemoteMac.Tests;

public class CryptoTests
{
    [Fact]
    public void AeadRoundTrip()
    {
        var p = new AeadCryptographyProvider("AES", "GCM", 1000);
        var ct = p.Encrypt("hemligt lösenord åäö", "mR3m");
        Assert.Equal("hemligt lösenord åäö", p.Decrypt(ct, "mR3m"));
        Assert.Throws<EncryptionException>(() => p.Decrypt(ct, "wrong"));
    }

    [Fact]
    public void DecryptsMRemoteNgProtectedAttribute()
    {
        // from mRemoteNG test vector confCons_v2_6.xml (KdfIterations 1000, password mR3m)
        var p = new AeadCryptographyProvider("AES", "GCM", 1000);
        var plain = p.Decrypt("8LmIO3+MWBY0zTmfjfOEdCGxhTAwnlohb1veTGNZFt6lAYvY2UOzWyjVzkx6V93smpbP0ZOuexN15u7rvwJEjawC", "mR3m");
        Assert.Equal("ThisIsNotProtected", plain);
    }

    [Fact]
    public void LegacyRoundTrip()
    {
        var p = new LegacyRijndaelCryptographyProvider();
        var ct = p.Encrypt("abc", "mR3m");
        Assert.Equal("abc", p.Decrypt(ct, "mR3m"));
    }
}

public class ConnectionsFileTests
{
    private static string Res(string name) => Path.Combine(AppContext.BaseDirectory, "Resources", name);

    [Fact]
    public void LoadsUnprotected26File()
    {
        var r = ConnectionsFile.Load(Res("confCons_v2_6.xml"));
        Assert.Equal(2.6, r.ConfVersion);
        Assert.NotEmpty(r.Root.Children);
        var folder = Assert.IsType<ContainerInfo>(r.Root.Children[0]);
        Assert.Equal("Folder1", folder.Name);
        Assert.Equal(ProtocolType.RDP, folder.Protocol); // ICA mapped to RDP
    }

    [Fact]
    public void ProtectedFileRequiresPassword()
    {
        Assert.Throws<ConnectionsFile.PasswordRequiredException>(() => ConnectionsFile.Load(Res("confCons_v2_6_passwordis_Password.xml")));
        var r = ConnectionsFile.Load(Res("confCons_v2_6_passwordis_Password.xml"), "Password");
        Assert.Equal("Password", r.Root.MasterPassword);
        Assert.NotEmpty(r.Root.Children);
    }

    [Fact]
    public void LoadsFullFileEncryption()
    {
        var r = ConnectionsFile.Load(Res("confCons_v2_6_fullencryption.xml"));
        Assert.True(r.FullFileEncryption);
        Assert.NotEmpty(r.Root.Children);
        var r2 = ConnectionsFile.Load(Res("confCons_v2_6_passwordis_Password_fullencryption.xml"), "Password");
        Assert.NotEmpty(r2.Root.Children);
    }

    [Fact]
    public void Loads5kIterations()
    {
        var r = ConnectionsFile.Load(Res("confCons_v2_6_5k-iterations.xml"));
        Assert.NotEmpty(r.Root.Children);
    }

    [Fact]
    public void SaveAndReloadRoundTrip()
    {
        var root = new RootNodeInfo();
        var folder = new ContainerInfo { Name = "Servers" };
        root.AddChild(folder);
        var sub = new ContainerInfo { Name = "Prod" };
        folder.AddChild(sub);
        var c = new ConnectionInfo { Name = "DC01", Hostname = "dc01.example.com", Username = "admin", Password = "s3cret", Domain = "EXAMPLE", Colors = RdpColors.Colors32Bit, RedirectClipboard = true, RedirectSound = RdpSounds.BringToThisComputer };
        c.Inheritance.Add("Username");
        sub.AddChild(c);

        var text = ConnectionsFile.SerializeToText(root);
        Assert.Contains("ConfVersion=\"2.9\"", text);
        Assert.DoesNotContain("s3cret", text);

        var r = ConnectionsFile.LoadFromText(text, null);
        var folder2 = Assert.IsType<ContainerInfo>(r.Root.Children[0]);
        var sub2 = Assert.IsType<ContainerInfo>(folder2.Children[0]);
        var c2 = sub2.Children[0];
        Assert.Equal("DC01", c2.Name);
        Assert.Equal("s3cret", c2.Password);
        Assert.Equal(RdpColors.Colors32Bit, c2.Colors);
        Assert.True(c2.RedirectClipboard);
        Assert.Equal(RdpSounds.BringToThisComputer, c2.RedirectSound);
        Assert.Contains("Username", c2.Inheritance);
        Assert.True(c2.InheritanceActive);
        Assert.Equal(c.ConstantId, c2.ConstantId);
    }

    [Fact]
    public void SaveWithMasterPasswordAndFullEncryption()
    {
        var root = new RootNodeInfo { MasterPassword = "topsecret" };
        root.AddChild(new ConnectionInfo { Name = "X", Hostname = "x", Password = "pw" });
        var text = ConnectionsFile.SerializeToText(root, fullFileEncryption: true);
        Assert.DoesNotContain("Hostname", text);
        Assert.Throws<ConnectionsFile.PasswordRequiredException>(() => ConnectionsFile.LoadFromText(text, null));
        var r = ConnectionsFile.LoadFromText(text, "topsecret");
        Assert.Equal("pw", r.Root.Children[0].Password);
    }
}
