# Changelog

All notable changes to mRemoteMac are documented here. The format follows [Keep a Changelog](https://keepachangelog.com)
and versions follow [Semantic Versioning](https://semver.org) via git tags (MinVer).

## [1.2.0] – 2026-10-12

### Added
- **Files over the clipboard, both ways.** Copy files or folders in Finder and paste them in the session: the
  server pulls their contents on demand (MS-RDPECLIP file streams, served by FreeRDP's own file-clipboard code, so
  size does not matter). Copy files in the session and they are fetched to this Mac in the background (up to the
  limit in Options ▸ RDP, 256 MB by default, larger sets ask first) and put on the Mac clipboard as files; paste in
  Finder to copy them. Progress and the result show in the notifications panel. Remote clipboard data is locked
  during a transfer when the server supports it.
- Clipboard diagnostics in the notifications panel (channel ready, offered formats, requests, transfers).

## [1.1.1] – 2026-10-12

### Changed
- New app icon: a centred, mirror-symmetric display on a stand with a green "connected" LED in the foot, drawn
  procedurally like before (`Services/IconRenderer.cs`).

## [1.1.0] – 2026-10-12

### Added
- **Encrypted connection database with history.** Connections now live in `connections.db` (SQLite, every page
  encrypted with ChaCha20-Poly1305 via SQLite3 Multiple Ciphers). The data key is wrapped with a key in the login
  keychain (no prompt at start) or, after File ▸ Set Master Password…, with PBKDF2-HMAC-SHA256 (600 000 rounds).
  Every save appends a revision with a change summary; File ▸ History… restores any of the last 200. Saves are
  transactional, so a crash can no longer leave a half-written file. See [docs/storage.md](docs/storage.md).
- First start imports an existing `confCons.xml` (from 1.0.x or any mRemoteNG) into the database and leaves the XML
  untouched. File ▸ Import mRemoteNG File… and File ▸ Export to mRemoteNG File… keep the format exchangeable (the
  export asks for a master password for the file), and an XML file can still be opened directly as the live store for
  people who share it with mRemoteNG on Windows.
- Unknown mRemoteNG secret attributes (e.g. `VNCProxyPassword`) are now decrypted on import and re-encrypted on
  export, so they survive a change of master password.

### Fixed
- Clipboard between Mac and session did not work against Windows hosts: the client must answer the server's
  Monitor Ready with a format list (even an empty one) before announcing anything, and must never announce before.
  The shim now follows the MS-RDPECLIP initialisation sequence, and text is announced as soon as the channel is ready.

## [1.0.1] – 2026-10-12

### Changed
- The app is now compiled ahead of time (.NET NativeAOT): one native executable plus the native libraries, no
  .NET runtime or IL in the bundle. The bundle shrinks from 152 MB to about 53 MB and starts faster. The code is
  trim-clean (compiled bindings, source-generated JSON settings, no runtime reflection over connection properties).
- The macOS application menu is titled "mRemoteMac" instead of Avalonia's default.

### Added
- Every property in the config panel now does something: idle timeout (with optional alert) disconnects after N
  minutes without input, Tab Color colours the tab marker, Connection Frame Color frames the session, Sound Quality
  is passed to the audio channel. Each property has help text in the panel. Fields without an equivalent in
  mRemoteMac (Panel, RDP Version, Enhanced Mode, Key Combinations, external tools) are hidden from the panel but
  kept untouched in the connection file.
- Clipboard: text copied on the Mac is pushed to the session automatically (NSPasteboard change polling while
  connected, not only when the session view gains focus), and new / quick-connect connections have clipboard
  redirection on by default. Text copied in the session already landed on the Mac clipboard.
- Analogue TV static behind the "Connecting…" overlay while a session is being established (Options ▸ Appearance
  to turn it off). Rendered into a small bitmap at 25 fps only while visible, so idle tabs cost nothing. The overlay
  stays up until the server has drawn its first frame, so there is no black panel between handshake and desktop.

### Changed
- The Protocol field offers RDP only. Connections imported with another protocol (SSH, VNC, …) stay in the file,
  but they are dimmed in the tree with an explanatory tooltip and their Connect commands are disabled.

## [1.0.0] – 2026-10-11

First public release.

### Added
- Native Apple Silicon app (.NET 10, Avalonia 11.3) with the mRemoteNG layout: connection tree, property-grid
  config panel, session tabs, quick connect, notifications, light and dark appearance.
- Embedded RDP sessions through FreeRDP 3.32 (RemoteFX / progressive graphics pipeline, dynamic resolution,
  remote cursors, clipboard text, audio, drive / printer / smart card redirection, RD Gateway, Restricted Admin,
  Remote Credential Guard, automatic reconnection).
- Full `confCons.xml` compatibility (ConfVersion 2.6–2.9, master password, full-file encryption, legacy import),
  verified against mRemoteNG's own test vectors.
- macOS keyboard layout detection, ⌘ as Ctrl/Win/Alt inside sessions, special-key menu.
- "Graphite + Indigo" design system, Lucide icon set, procedurally rendered app icon.
- Scripted UI audit harness for debug builds; CI builds, tests and packages a signed `.dmg` and `.zip` on tags.

### Known limitations
- RDP only (SSH, VNC and other mRemoteNG protocols are not opened; their entries are preserved in the file).
- No H.264 (AVC) decoding; the progressive RemoteFX codec is used instead.
- Ad-hoc code signature: the first launch requires right-click ▸ Open.
