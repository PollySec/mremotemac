# Changelog

All notable changes to mRemoteMac are documented here. The format follows [Keep a Changelog](https://keepachangelog.com)
and versions follow [Semantic Versioning](https://semver.org) via git tags (MinVer).

## [Unreleased]

### Changed
- The app is now compiled ahead of time (.NET NativeAOT): one native executable plus the native libraries, no
  .NET runtime or IL in the bundle. The bundle shrinks from 152 MB to about 53 MB and starts faster. The code is
  trim-clean (compiled bindings, source-generated JSON settings, no runtime reflection over connection properties).
- The macOS application menu is titled "mRemoteMac" instead of Avalonia's default.

### Added
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
