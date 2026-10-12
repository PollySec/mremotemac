using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using MRemoteMac.Models;

namespace MRemoteMac.Config;

/// <summary>
/// The encrypted connection database: SQLite3 Multiple Ciphers (ChaCha20-Poly1305 pages) over SQLite in WAL mode,
/// keyed with a random 32-byte data key (see <see cref="VaultHeader"/> for how that key is protected).
/// Nodes are stored as mRemoteNG attribute records (<see cref="ConnectionsFile.NodeAttributes"/>), so import and
/// export are lossless, and every save appends a compressed snapshot to the revision history.
/// </summary>
public sealed partial class SqliteConnectionStore : IConnectionStore
{
    public const string DefaultFileName = "connections.db";
    public const int SchemaVersion = 1;
    public const int MaxRevisions = 200;

    public string Location { get; }
    public string DisplayName => Path.GetFileName(Location);
    public bool SupportsHistory => true;
    public bool HasMasterPassword { get; internal set; }

    /// <summary>The data-encryption key (held to re-wrap it when the unlock mode changes).</summary>
    internal byte[] DataKey { get; }

    private readonly SqliteConnection _db;
    private readonly object _lock = new();

    /// <summary>Thrown when the key does not open the database (wrong password, foreign device key, or not a database).</summary>
    public sealed class WrongKeyException(string detail) : Exception("The database could not be opened with this key (" + detail + ").");

    private SqliteConnectionStore(string path, SqliteConnection db, byte[] key)
    {
        Location = Path.GetFullPath(path);
        _db = db;
        DataKey = (byte[])key.Clone(); // owned copy: zeroed on dispose without touching the caller's buffer
    }

    /// <summary>Opens an existing database or creates a new one with the 32-byte <paramref name="key"/>.</summary>
    public static SqliteConnectionStore Open(string path, byte[] key)
    {
        if (key.Length != 32) throw new ArgumentException("The data key is 32 bytes", nameof(key));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var existed = File.Exists(path);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        db.Open();
        try
        {
            // raw key: no KDF inside the database engine, the vault header handles key protection
            Exec(db, "PRAGMA key = \"x'" + Convert.ToHexString(key) + "'\"");
            try { Scalar(db, "SELECT count(*) FROM sqlite_master"); }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 26 /* SQLITE_NOTADB */ or 11 /* SQLITE_CORRUPT */)
            {
                throw new WrongKeyException($"{ex.SqliteErrorCode}/{ex.SqliteExtendedErrorCode}: {ex.Message}");
            }
            Exec(db, "PRAGMA journal_mode = WAL");
            Exec(db, "PRAGMA synchronous = NORMAL");
            Exec(db, "PRAGMA temp_store = MEMORY"); // temporary tables never touch the disk unencrypted
            Exec(db, "PRAGMA foreign_keys = ON");
            var store = new SqliteConnectionStore(path, db, key);
            store.EnsureSchema(existed);
            return store;
        }
        catch
        {
            db.Dispose();
            if (!existed) TryDelete(path);
            throw;
        }
    }

    public static bool LooksLikeDatabase(string path)
        => path.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ schema

    private void EnsureSchema(bool existed)
    {
        Exec(_db, """
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS nodes (
                id TEXT PRIMARY KEY,
                parent_id TEXT NULL REFERENCES nodes(id) ON DELETE CASCADE,
                position INTEGER NOT NULL,
                type TEXT NOT NULL,
                name TEXT NOT NULL,
                attributes TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS nodes_parent ON nodes(parent_id, position);
            CREATE TABLE IF NOT EXISTS revisions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                created_at TEXT NOT NULL,
                summary TEXT NOT NULL,
                connections INTEGER NOT NULL,
                folders INTEGER NOT NULL,
                snapshot BLOB NOT NULL);
            """);
        var version = GetMeta("schema_version");
        if (version is null)
        {
            SetMeta("schema_version", SchemaVersion.ToString(CultureInfo.InvariantCulture));
            SetMeta("created_at", DateTimeOffset.UtcNow.ToString("O"));
            SetMeta("root_name", "Connections");
        }
        else if (int.Parse(version, CultureInfo.InvariantCulture) > SchemaVersion)
            throw new InvalidDataException($"The database was written by a newer mRemoteMac (schema {version}).");
        _ = existed;
    }

    private string? GetMeta(string key)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private void SetMeta(string key, string value)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT INTO meta(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ records

    /// <summary>One node row; attributes are exactly what the mRemoteNG serializer would write (secrets in plain text).</summary>
    public sealed class NodeRecord
    {
        public required string Id { get; init; }
        public string? ParentId { get; init; }
        public int Position { get; init; }
        public required string Type { get; init; }
        public required string Name { get; init; }
        public required List<KeyValuePair<string, string>> Attributes { get; init; }
    }

    private sealed class Snapshot
    {
        public required string RootName { get; init; }
        public required List<NodeRecord> Nodes { get; init; }
    }

    private static List<NodeRecord> Flatten(RootNodeInfo root)
    {
        var list = new List<NodeRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Walk(ContainerInfo parent, string? parentId)
        {
            var pos = 0;
            foreach (var n in parent.Children)
            {
                // duplicate ids (the same file imported twice) get a fresh id; the history then shows the node as added
                if (string.IsNullOrEmpty(n.ConstantId) || !seen.Add(n.ConstantId)) { n.ConstantId = Guid.NewGuid().ToString(); seen.Add(n.ConstantId); }
                list.Add(new NodeRecord
                {
                    Id = n.ConstantId, ParentId = parentId, Position = pos++, Type = n.IsContainer ? "Container" : "Connection",
                    Name = n.Name, Attributes = ConnectionsFile.NodeAttributes(n, static s => s),
                });
                if (n is ContainerInfo c) Walk(c, n.ConstantId);
            }
        }
        Walk(root, null);
        return list;
    }

    // ------------------------------------------------------------------ load / save

    public RootNodeInfo Load()
    {
        lock (_lock)
        {
            var rootName = GetMeta("root_name") ?? "Connections";
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id, parent_id, position, type, name, attributes FROM nodes ORDER BY parent_id IS NOT NULL, position";
            using var reader = cmd.ExecuteReader();
            var records = new List<NodeRecord>();
            while (reader.Read())
            {
                records.Add(new NodeRecord
                {
                    Id = reader.GetString(0), ParentId = reader.IsDBNull(1) ? null : reader.GetString(1), Position = reader.GetInt32(2),
                    Type = reader.GetString(3), Name = reader.GetString(4),
                    Attributes = JsonSerializer.Deserialize(reader.GetString(5), StoreJsonContext.Default.ListKeyValuePairStringString) ?? [],
                });
            }
            return BuildTreeOrdered(rootName, records);
        }
    }

    // Records come out ordered by (is child, position); children of nested folders need their parent inserted first,
    // which the ORDER BY does not guarantee across levels, so attach in dependency order.
    private static RootNodeInfo BuildTreeOrdered(string rootName, List<NodeRecord> records)
    {
        var byParent = records.GroupBy(r => r.ParentId ?? "").ToDictionary(g => g.Key, g => g.OrderBy(r => r.Position).ToList(), StringComparer.Ordinal);
        var root = new RootNodeInfo { Name = rootName };
        void Attach(ContainerInfo parent, string key)
        {
            if (!byParent.TryGetValue(key, out var children)) return;
            foreach (var r in children)
            {
                var node = ConnectionsFile.NewNode(r.Type);
                ConnectionsFile.ReadAttributes(node, r.Attributes, static s => s);
                node.ConstantId = r.Id;
                parent.AddChild(node);
                if (node is ContainerInfo c) Attach(c, r.Id);
            }
        }
        Attach(root, "");
        return root;
    }

    public void Save(RootNodeInfo root, string? summary = null)
    {
        lock (_lock)
        {
            var records = Flatten(root);
            var previous = LatestSnapshot();
            summary ??= Describe(previous?.Nodes, records);
            using var tx = _db.BeginTransaction();
            Exec(_db, "DELETE FROM nodes", tx);
            using (var cmd = _db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO nodes(id, parent_id, position, type, name, attributes) VALUES($id, $parent, $pos, $type, $name, $attrs)";
                var pId = cmd.Parameters.Add("$id", SqliteType.Text);
                var pParent = cmd.Parameters.Add("$parent", SqliteType.Text);
                var pPos = cmd.Parameters.Add("$pos", SqliteType.Integer);
                var pType = cmd.Parameters.Add("$type", SqliteType.Text);
                var pName = cmd.Parameters.Add("$name", SqliteType.Text);
                var pAttrs = cmd.Parameters.Add("$attrs", SqliteType.Text);
                foreach (var r in records) // parents precede children in traversal order, satisfying the foreign key
                {
                    pId.Value = r.Id; pParent.Value = (object?)r.ParentId ?? DBNull.Value; pPos.Value = r.Position;
                    pType.Value = r.Type; pName.Value = r.Name;
                    pAttrs.Value = JsonSerializer.Serialize(r.Attributes, StoreJsonContext.Default.ListKeyValuePairStringString);
                    cmd.ExecuteNonQuery();
                }
            }
            using (var cmd = _db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO meta(key, value) VALUES('root_name', $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
                cmd.Parameters.AddWithValue("$v", root.Name);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = _db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO revisions(created_at, summary, connections, folders, snapshot) VALUES($at, $summary, $conns, $folders, $snap)";
                cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("$summary", summary);
                cmd.Parameters.AddWithValue("$conns", records.Count(r => r.Type == "Connection"));
                cmd.Parameters.AddWithValue("$folders", records.Count(r => r.Type == "Container"));
                cmd.Parameters.AddWithValue("$snap", Compress(new Snapshot { RootName = root.Name, Nodes = records }));
                cmd.ExecuteNonQuery();
            }
            Exec(_db, $"DELETE FROM revisions WHERE id NOT IN (SELECT id FROM revisions ORDER BY id DESC LIMIT {MaxRevisions})", tx);
            tx.Commit();
            // fold the WAL into the main file so that copying connections.db alone is always complete (cheap at this size)
            Exec(_db, "PRAGMA wal_checkpoint(TRUNCATE)");
        }
    }

    public IReadOnlyList<RevisionInfo> History()
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id, created_at, summary, connections, folders FROM revisions ORDER BY id DESC";
            using var reader = cmd.ExecuteReader();
            var list = new List<RevisionInfo>();
            while (reader.Read())
                list.Add(new RevisionInfo(reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4)));
            return list;
        }
    }

    public RootNodeInfo LoadRevision(long id)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT snapshot FROM revisions WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            var blob = cmd.ExecuteScalar() as byte[] ?? throw new KeyNotFoundException($"Revision {id} does not exist.");
            var snap = Decompress(blob);
            return BuildTreeOrdered(snap.RootName, snap.Nodes);
        }
    }

    private Snapshot? LatestSnapshot()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT snapshot FROM revisions ORDER BY id DESC LIMIT 1";
        return cmd.ExecuteScalar() is byte[] blob ? Decompress(blob) : null;
    }

    /// <summary>Human-readable change summary for the history list: what was added, removed and changed.</summary>
    internal static string Describe(List<NodeRecord>? before, List<NodeRecord> after)
    {
        if (before is null) return after.Count == 0 ? "Created" : $"Initial import: {after.Count(r => r.Type == "Connection")} connections";
        var prev = before.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var next = after.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var added = after.Where(r => !prev.ContainsKey(r.Id)).Select(r => r.Name).ToList();
        var removed = before.Where(r => !next.ContainsKey(r.Id)).Select(r => r.Name).ToList();
        var changed = after.Where(r => prev.TryGetValue(r.Id, out var p) && (p.ParentId != r.ParentId || !p.Attributes.SequenceEqual(r.Attributes))).Select(r => r.Name).ToList();
        var moved = after.Count(r => prev.TryGetValue(r.Id, out var p) && p.ParentId == r.ParentId && p.Position != r.Position && p.Attributes.SequenceEqual(r.Attributes));
        var parts = new List<string>();
        if (added.Count > 0) parts.Add("Added " + Names(added));
        if (changed.Count > 0) parts.Add("Changed " + Names(changed));
        if (removed.Count > 0) parts.Add("Removed " + Names(removed));
        if (moved > 0) parts.Add(moved == 1 ? "Reordered 1 item" : $"Reordered {moved} items");
        return parts.Count == 0 ? "No changes" : string.Join("; ", parts);

        static string Names(List<string> names) => names.Count <= 3 ? string.Join(", ", names) : $"{string.Join(", ", names.Take(3))} and {names.Count - 3} more";
    }

    // ------------------------------------------------------------------ helpers

    private static byte[] Compress(Snapshot snapshot)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            JsonSerializer.Serialize(gz, snapshot, StoreJsonContext.Default.Snapshot);
        return ms.ToArray();
    }

    private static Snapshot Decompress(byte[] blob)
    {
        using var gz = new GZipStream(new MemoryStream(blob), CompressionMode.Decompress);
        return JsonSerializer.Deserialize(gz, StoreJsonContext.Default.Snapshot) ?? throw new InvalidDataException("Corrupt revision snapshot.");
    }

    private static void Exec(SqliteConnection db, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        lock (_lock) _db.Dispose();
        CryptographicOperations.ZeroMemory(DataKey);
    }

    [JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
    [JsonSerializable(typeof(List<KeyValuePair<string, string>>))]
    [JsonSerializable(typeof(Snapshot))]
    private sealed partial class StoreJsonContext : JsonSerializerContext;
}
