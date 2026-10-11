# Changelog

All notable changes to mRemoteMac are documented here. The format follows [Keep a Changelog](https://keepachangelog.com)
and versions follow [Semantic Versioning](https://semver.org) via git tags (MinVer).

## [Unreleased]

### Added
- Analogue TV static behind the "Connecting…" overlay while a session is being established (Options ▸ Appearance
  to turn it off). Rendered into a small bitmap at 25 fps only while visible, so idle tabs cost nothing.

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
