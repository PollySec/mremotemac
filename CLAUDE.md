# mRemoteMac – instructions for Claude

- **Identity:** this project belongs to **PollySec Labs AB**. Author/committer for git is `Anders Persson <anders@pollysec.com>`.
  No other company name or e-mail identity may appear anywhere (commits, docs, metadata, test data, screenshots).
- GitHub home: `PollySec/mremotemac` (public). Bundle id `com.pollysec.mremotemac`.
- Build: `./native/build-deps.sh` (once), `./native/build.sh`, `dotnet build src/MRemoteMac`; tests `dotnet test tests/MRemoteMac.Tests`.
- Storage: `docs/storage.md`. Connections are in an encrypted SQLite database (`Config/SqliteConnectionStore`, key model in
  `Config/VaultHeader` + `Config/ConnectionStores`); XML stays import/export/alternative store. Both formats go through
  `ConnectionsFile.NodeAttributes` / `ReadAttributes` – never serialize a node any other way.
- Clipboard testing without a Windows host: `tools/clipsrv/build.sh` builds FreeRDP's sample server with an rdpclip-like
  file and text clipboard (`CLIPSRV_OFFER`, `CLIPSRV_TEXT`, `CLIPSRV_PASTE`, `CLIPSRV_DELAY_MS`, `CLIPSRV_MAX_REQUEST`, `CLIPSRV_SAVE`); run it from the sample's
  source directory like sfreerdp-server. The download strategy is described at the end of `docs/performance.md`.
- Performance: read `docs/performance.md` before touching the frame path; `MRM_FRAMEBENCH=WxH@fps[:dirty%]` (debug) measures it.
- UI audit: debug builds accept `MRM_SCRIPT=<script> MRM_OUT=<dir> MRM_HOME=<settings dir>` (see `Services/UiTestRunner.cs`).
- Code rules: .NET 10 best practices, no `async void` (use `AsyncHelper.Fire`), RDP-thread events are marshalled with `Dispatcher.UIThread.Post`,
  native interop via `LibraryImport`, analyzers at `latest-recommended` must stay warning-free.
- The app ships as NativeAOT (`packaging/make-app.sh`). Keep everything trim/AOT-clean: XAML bindings need `x:DataType`
  (compiled bindings are on by default), JSON via source generators, no `Enum.GetValues(Type)` / `GetType().GetProperty`,
  reflection over `ConnectionInfo` only through `ConnectionInfo.FindProperty` / `GridProperties`. `IsAotCompatible` is on
  in Release, so `dotnet build -c Release -warnaserror` must pass. Verify a published build with
  `MRM_HOME=… MRM_CONNECT_NODE=… MRM_SCREENSHOT=… MRM_EXIT=1 dist/mRemoteMac.app/Contents/MacOS/mRemoteMac`.
- Commit messages end with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
