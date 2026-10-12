# Connection storage

Since 1.1 the connection tree lives in an encrypted SQLite database. mRemoteNG's `confCons.xml` remains fully
supported for import, export and as an alternative live store.

## Files

| File | Purpose |
|---|---|
| `~/Library/Application Support/mRemoteMac/connections.db` | The vault: SQLite, every page encrypted (SQLite3 Multiple Ciphers, ChaCha20-Poly1305), WAL journal |
| `connections.db.vault` | Plaintext header: unlock mode, KDF parameters and the **wrapped** data key |
| login keychain item `com.pollysec.mremotemac` / `vault:<hash>` | Key-encryption key in keychain mode |
| `confCons.xml` (1.0.x) | Left untouched after the one-time import at first start |

## Keys

```
data key (32 random bytes)  ──encrypts──▶  connections.db pages
        ▲
        │ AES-256-GCM wrap (nonce, tag, AAD "mRemoteMac vault v1"), stored in connections.db.vault
        │
key-encryption key ◀── keychain mode: 32 random bytes in the login keychain (no prompt at start)
                   ◀── password mode: PBKDF2-HMAC-SHA256, 600 000 rounds, 16-byte salt from the header
```

Changing the master password or switching modes re-wraps the data key only; the database is never rewritten.
A wrong password is detected by the GCM tag before SQLite is touched. The database engine gets the raw key
(`PRAGMA key = "x'…'"`), so no second KDF runs on every open.

Moving the vault to another Mac: set a master password first (File ▸ Set Master Password…), then copy both
`connections.db` and `connections.db.vault`. A keychain-mode vault can only be unlocked on the Mac that holds its
keychain item. Export to mRemoteNG XML is always available as the portable format.

## History

Every save appends a revision: timestamp, a change summary ("Added Web02; Changed DC01"), node counts and a gzip
snapshot of all records. The newest 200 revisions are kept. File ▸ History… lists them and restores one; the restore
itself becomes a revision, so nothing is lost.

## Schema

```sql
meta(key TEXT PRIMARY KEY, value TEXT)          -- schema_version, created_at, root_name
nodes(id TEXT PRIMARY KEY, parent_id TEXT REFERENCES nodes(id) ON DELETE CASCADE,
      position INTEGER, type TEXT, name TEXT, attributes TEXT /* JSON [{name, value}…] */)
revisions(id INTEGER PRIMARY KEY, created_at TEXT, summary TEXT, connections INTEGER, folders INTEGER, snapshot BLOB)
```

`attributes` is exactly the attribute list mRemoteNG writes for a node (`ConnectionsFile.NodeAttributes`), so the
database and the XML serializer share one code path and import/export is lossless, including attributes this app
does not use (VNC, SSH, PuTTY fields).

## Why not SQLCipher

The only free SQLCipher build for .NET (`SQLitePCLRaw.bundle_e_sqlcipher`) is deprecated and frozen at SQLite 3.39
(2022, with later CVEs). SQLite3 Multiple Ciphers is MIT-licensed, tracks current SQLite, ships an arm64 dylib and
is recognised by Microsoft.Data.Sqlite. Its `sqlcipher` cipher scheme can read SQLCipher 4 files if ever needed.

## Code signing and the keychain

A file-based keychain item records the designated requirement of the app that created it. With an ad-hoc signature
that requirement is the build's cdhash, so **every rebuild counts as a new app** and macOS asks once whether
mRemoteMac may use the item ("Always Allow" whitelists that build). Release builds signed with a stable identity
(`CODESIGN_IDENTITY` in `packaging/make-app.sh`) do not have this problem. The scripted UI audit uses a key file
instead of the keychain so it can run unattended.
