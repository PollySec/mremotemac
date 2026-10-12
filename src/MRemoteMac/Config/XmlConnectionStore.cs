using MRemoteMac.Models;

namespace MRemoteMac.Config;

/// <summary>An mRemoteNG confCons.xml file: full compatibility, no history.</summary>
public sealed class XmlConnectionStore : IConnectionStore
{
    public string Location { get; }
    public string DisplayName => Path.GetFileName(Location);
    public bool SupportsHistory => false;
    public bool HasMasterPassword => _root?.IsProtected == true;

    public ICryptographyProvider Crypto { get; private set; }
    public bool FullFileEncryption { get; private set; }
    private RootNodeInfo? _root;
    private readonly string? _masterPassword;

    /// <summary>Opens an existing file (throws <see cref="ConnectionsFile.PasswordRequiredException"/> when protected).</summary>
    public XmlConnectionStore(string path, string? masterPassword = null)
    {
        Location = Path.GetFullPath(path);
        _masterPassword = masterPassword;
        Crypto = new AeadCryptographyProvider();
    }

    /// <summary>A store for a file that does not exist yet.</summary>
    public static XmlConnectionStore CreateNew(string path) => new(path) { _root = new RootNodeInfo() };

    public RootNodeInfo Load()
    {
        if (_root is not null) return _root; // the opener validated the password already; the file is read once
        if (!File.Exists(Location)) return _root ??= new RootNodeInfo();
        var r = ConnectionsFile.Load(Location, _masterPassword);
        Crypto = r.Crypto;
        FullFileEncryption = r.FullFileEncryption;
        _root = r.Root;
        return r.Root;
    }

    public void Save(RootNodeInfo root, string? summary = null)
    {
        _root = root;
        Directory.CreateDirectory(Path.GetDirectoryName(Location)!);
        ConnectionsFile.Save(Location, root, Crypto, FullFileEncryption);
    }

    public IReadOnlyList<RevisionInfo> History() => [];

    public RootNodeInfo LoadRevision(long id) => throw new NotSupportedException("XML files keep no history.");

    public void Dispose() { }
}
