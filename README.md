# mRemoteMac

A native Apple Silicon (arm64) port of [mRemoteNG](https://github.com/mRemoteNG/mRemoteNG) for macOS, written in
C# on .NET 10 with [Avalonia UI](https://avaloniaui.net). It supports the **RDP** protocol only, rendered in-app through
[FreeRDP 3](https://www.freerdp.com) via a small C shim.

The UI follows mRemoteNG's default layout: connection tree and config panel on the left, tabbed sessions in the
middle, notifications at the bottom, menu bar and quick-connect toolbar on top. It reads and writes mRemoteNG's
`confCons.xml` (ConfVersion 2.6 – 2.9, AES-GCM encrypted passwords, master password and full-file encryption), so a
connection file can be shared with mRemoteNG on Windows.

## Build

Prerequisites: Xcode command line tools, .NET 10 SDK. CMake, OpenSSL and FreeRDP are built from source by the
scripts below (no Homebrew needed).

```sh
# 1. dependencies (once) -> ../mRemoteMac-deps
./native/build-deps.sh

# 2. native shim + app
./native/build.sh
dotnet build src/MRemoteMac

# 3. run
dotnet run --project src/MRemoteMac

# 4. optional: self-contained mRemoteMac.app in ./dist
./packaging/make-app.sh
```

## Layout

| Path | Purpose |
|---|---|
| `native/mrrdp.[ch]` | C shim over libfreerdp-client (session, framebuffer, input, cursor, clipboard, display control) |
| `src/MRemoteMac/Rdp` | P/Invoke binding, managed `RdpSession`, `RdpView` Avalonia control, scancode mapping |
| `src/MRemoteMac/Config` | `confCons.xml` reader/writer and the mRemoteNG-compatible crypto providers |
| `src/MRemoteMac/Models` | `ConnectionInfo` / `ContainerInfo` / `RootNodeInfo` with inheritance |
| `src/MRemoteMac/Views` | Main window (menu, toolbars, tree, config grid, session tabs, notifications) |
| `tests/` | xunit tests using mRemoteNG's own test vectors |

Settings and the default connection file are stored in `~/Library/Application Support/mRemoteMac/`.

## License

GPL-2.0, like mRemoteNG (see `LICENSE`). FreeRDP is Apache-2.0. Icons are taken from mRemoteNG.
