using MRemoteMac.Models;

namespace MRemoteMac.Config;

/// <summary>One saved state of the connection tree, as kept by stores that support history.</summary>
public sealed record RevisionInfo(long Id, DateTimeOffset CreatedAt, string Summary, int Connections, int Folders);

/// <summary>
/// Where the connection tree lives: the encrypted SQLite database (default) or an mRemoteNG confCons.xml file.
/// The view model talks to this interface only, so both formats share every feature above it.
/// </summary>
public interface IConnectionStore : IDisposable
{
    /// <summary>Absolute path of the backing file.</summary>
    string Location { get; }

    /// <summary>Short name for the title bar.</summary>
    string DisplayName { get; }

    /// <summary>True when <see cref="History"/> and <see cref="LoadRevision"/> are available.</summary>
    bool SupportsHistory { get; }

    /// <summary>True when the store is protected by a user-chosen master password (as opposed to a device key).</summary>
    bool HasMasterPassword { get; }

    RootNodeInfo Load();

    /// <summary>Persists the tree atomically. <paramref name="summary"/> is recorded with the revision when supported.</summary>
    void Save(RootNodeInfo root, string? summary = null);

    IReadOnlyList<RevisionInfo> History();

    RootNodeInfo LoadRevision(long id);
}
