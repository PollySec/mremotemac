using MRemoteMac.Config;
using MRemoteMac.Models;
using Xunit;

namespace MRemoteMac.Tests;

/// <summary>The encrypted SQLite store: round trip, history, key handling and lossless mRemoteNG import/export.</summary>
public sealed class SqliteStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mrm-tests-" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "connections.db");

    static SqliteStoreTests() => SQLitePCL.Batteries_V2.Init();
    public SqliteStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static RootNodeInfo SampleTree()
    {
        var root = new RootNodeInfo();
        var folder = new ContainerInfo { Name = "Lab", Icon = "Infrastructure" };
        root.AddChild(folder);
        var dc = new ConnectionInfo { Name = "DC01", Hostname = "dc01.lab", Username = "admin", Password = "s3cret åäö", Domain = "LAB", RedirectClipboard = true, TabColor = "#D9534F" };
        dc.Inheritance.Add(nameof(ConnectionInfo.Colors));
        folder.AddChild(dc);
        folder.AddChild(new ConnectionInfo { Name = "FS01", Hostname = "fs01.lab", RdGatewayHostname = "gw.lab", RdGatewayPassword = "gwpw" });
        root.AddChild(new ConnectionInfo { Name = "Standalone", Hostname = "10.0.0.5", Port = 3390, UnknownAttributes = { ["VNCCompression"] = "CompLow" } });
        return root;
    }

    private static byte[] NewKey() => System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void RoundTripsTheTreeLosslessly()
    {
        var key = NewKey();
        var original = SampleTree();
        var expected = ConnectionsFile.SerializeToText(original);
        using (var store = SqliteConnectionStore.Open(DbPath, key))
            store.Save(original);

        using var reopened = SqliteConnectionStore.Open(DbPath, key);
        var root = reopened.Load();
        var actual = ConnectionsFile.SerializeToText(root);
        // the mRemoteNG serializer encrypts passwords with a random nonce: compare with secrets masked
        Assert.Equal(Mask(expected), Mask(actual));
        var dc = root.Descendants().First(n => n.Name == "DC01");
        Assert.Equal("s3cret åäö", dc.Password);
        Assert.True(dc.Inherits(nameof(ConnectionInfo.Colors)) || dc.Inheritance.Contains(nameof(ConnectionInfo.Colors)));
        Assert.Equal("CompLow", root.Descendants().First(n => n.Name == "Standalone").UnknownAttributes["VNCCompression"]);
        Assert.Equal("gwpw", root.Descendants().First(n => n.Name == "FS01").RdGatewayPassword);
    }

    private static string Mask(string xml) => System.Text.RegularExpressions.Regex.Replace(xml, "(Password|RDGatewayPassword|RDGatewayAccessToken|Protected)=\"[^\"]*\"", "$1=\"*\"");

    [Fact]
    public void EverySaveAppendsARevisionThatCanBeRestored()
    {
        using var store = SqliteConnectionStore.Open(DbPath, NewKey());
        var tree = SampleTree();
        store.Save(tree);
        tree.Descendants().First(n => n.Name == "DC01").Hostname = "dc01.new";
        store.Save(tree);
        ((ContainerInfo)tree.Children[0]).RemoveChild(tree.Descendants().First(n => n.Name == "FS01"));
        store.Save(tree);

        var history = store.History();
        Assert.Equal(3, history.Count);
        Assert.StartsWith("Removed FS01", history[0].Summary);
        Assert.StartsWith("Changed DC01", history[1].Summary);
        Assert.StartsWith("Initial import", history[2].Summary);
        Assert.Equal(2, history[0].Connections);

        var first = store.LoadRevision(history[2].Id);
        Assert.Equal("dc01.lab", first.Descendants().First(n => n.Name == "DC01").Hostname);
        Assert.Contains(first.Descendants(), n => n.Name == "FS01");
    }

    [Fact]
    public void WrongKeyIsRejected()
    {
        var key = NewKey();
        using (var store = SqliteConnectionStore.Open(DbPath, key)) store.Save(SampleTree());
        Assert.Throws<SqliteConnectionStore.WrongKeyException>(() => SqliteConnectionStore.Open(DbPath, NewKey()));
        using var again = SqliteConnectionStore.Open(DbPath, key);
        Assert.Equal(3, again.Load().Descendants().Count(n => !n.IsContainer));
    }

    [Fact]
    public void FileIsEncryptedOnDisk()
    {
        using (var store = SqliteConnectionStore.Open(DbPath, NewKey())) store.Save(SampleTree());
        var head = File.ReadAllBytes(DbPath).Take(16).ToArray();
        Assert.NotEqual("SQLite format 3\0"u8.ToArray(), head);
        Assert.DoesNotContain("dc01.lab", File.ReadAllText(DbPath, System.Text.Encoding.Latin1));
    }

    [Fact]
    public void ImportedMRemoteNgFileExportsIdentically()
    {
        var xml = Path.Combine(AppContext.BaseDirectory, "Resources", "confCons_v2_6.xml");
        var imported = ConnectionsFile.Load(xml);
        using var store = SqliteConnectionStore.Open(DbPath, NewKey());
        store.Save(imported.Root);
        var exported = ConnectionsFile.SerializeToText(store.Load(), imported.Crypto);
        Assert.Equal(Mask(ConnectionsFile.SerializeToText(imported.Root, imported.Crypto)), Mask(exported));
    }

    [Fact]
    public void RevisionsArePruned()
    {
        using var store = SqliteConnectionStore.Open(DbPath, NewKey());
        var tree = SampleTree();
        for (var i = 0; i < SqliteConnectionStore.MaxRevisions + 20; i++) { tree.Name = "R" + i; store.Save(tree); }
        Assert.Equal(SqliteConnectionStore.MaxRevisions, store.History().Count);
    }
}
