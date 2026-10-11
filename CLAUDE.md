# mRemoteMac – instructions for Claude

- **Identity:** this project belongs to **PollySec Labs AB**. Author/committer for git is `Anders Persson <anders@pollysec.com>`.
  No other company name or e-mail identity may appear anywhere (commits, docs, metadata, test data, screenshots).
- GitHub home: `PollySec/mremotemac` (public). Bundle id `com.pollysec.mremotemac`.
- Build: `./native/build-deps.sh` (once), `./native/build.sh`, `dotnet build src/MRemoteMac`; tests `dotnet test tests/MRemoteMac.Tests`.
- UI audit: debug builds accept `MRM_SCRIPT=<script> MRM_OUT=<dir> MRM_HOME=<settings dir>` (see `Services/UiTestRunner.cs`).
- Code rules: .NET 10 best practices, no `async void` (use `AsyncHelper.Fire`), RDP-thread events are marshalled with `Dispatcher.UIThread.Post`,
  native interop via `LibraryImport`, analyzers at `latest-recommended` must stay warning-free.
- Commit messages end with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
