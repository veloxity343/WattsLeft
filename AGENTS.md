# Working on WattsLeft

Instructions for AI coding agents (and a quick start for people). WattsLeft is a Windows tray app showing the battery of game controllers, mice, keyboards and headsets. It was called ControllerHP before 0.4.0.

## Read first

- [docs/dev/architecture.md](docs/dev/architecture.md): how the code is laid out and why.
- [docs/dev/windows-notes.md](docs/dev/windows-notes.md): the undocumented Windows details the app relies on, the quirks it works around, and the build and release lessons. **Read it before touching a battery source, the tray, or `.github/workflows`.**
- [docs/dev/diagnostics.md](docs/dev/diagnostics.md): the command-line flags for checking devices and rendering the interface without hardware.
- [CHANGELOG.md](CHANGELOG.md): what changed, and how releases are cut.

## Layout

- `src/WattsLeft.Core`: everything that isn't interface: the battery sources (`Battery/`, makers' protocols in `Battery/Vendor/`), settings (registry), history, stats, logs.
- `src/WattsLeft`: the Avalonia app, drawn in code with no XAML: the tray (`TrayController.cs`, `Platform/Windows/NotifyIcons.cs`), pop-ups, flyout, menu and settings window (`Ui/`).
- `docs/wiki`: the user guide. `docs/dev`: developer docs.

## Building and checking

- `dotnet build WattsLeft.sln -c Release` must finish with **0 warnings**: the trimming and AOT analysers are on, and their warnings mean a release would break.
- Releases are compiled to native code (Native AOT), which only works on Windows, so CI does it. Local builds are ordinary .NET builds; `deploy.sh` builds from WSL, installs to `%LOCALAPPDATA%\Programs\WattsLeft` and starts it.
- There are no automated tests. Check a change with the design aids (`--preview-settings`, `--preview-tray`, `--preview-icons`, `--list`) and on real devices.

## Rules

- **Only ever read from devices.** Never send a device a command that changes its state, and never send a guessed command to find something out.
- **Keep `WattsLeft.exe` and its install folder as they are.** Windows remembers the tray icon's place by the exe's path.
- **No network access beyond updates.** While the user has update checks on, the app makes one HTTPS request a day to GitHub and reads only the redirect address (`UpdateCheck.cs`). It downloads a release only after the user confirms, only from this repository's release, and installs it only if it matches the `.sha256` file published with it (`UpdateInstall.cs`). Every release must have that file: CI makes it. Ask the maintainer before adding anything else that goes online.
- **Shared logic lives in one place.** Talking to a HID device (`HidLink`), when to ask (`PollSchedule`), Bluetooth devices (`BluetoothDevices`), opening a device (`DeviceFile`), listening for reports (`ReportListener`), charging trends (`ChargeTrends`), and Windows calls the interface needs (`Platform/Windows/Win32.cs`). A new source or device supplies only what's its own (requests, how to read answers, intervals and timeouts), never a copy of these. Anything touched from more than one thread (polls, listeners, diagnostics, the tray) takes a lock or runs one at a time.
- **Don't add dependencies lightly.** The app has no runtime dependencies beyond Windows itself, and releases are one self-contained download; keep it that way.
- **Commits are the maintainer's own:** no AI co-author trailers or mentions of AI in commit messages or pull requests.
- **Push only to `main`, and only when the maintainer asks.** Never push work branches.
- **Every user-visible change gets a line in `CHANGELOG.md`** under Unreleased, and the wiki is kept in step with the app.

## Writing

Docs, changelog and text in the app use plain, British English ("colour", "licence"), short sentences, and the words a user would use. Say what something does for the user before how it works.

## Releasing

1. Move the changelog's Unreleased entries into `## [x.y.z] - <date>`, leave an empty Unreleased above it, and update the comparison links at the bottom.
2. Bump `<Version>` in `src/WattsLeft/WattsLeft.csproj`.
3. Commit, push `main`, and wait for CI to pass.
4. Tag `vx.y.z` on that commit and push the tag. CI builds, tests and publishes the release with the changelog section as its notes. Tags with a hyphen (`v1.0.0-beta.1`) become pre-releases.
5. Check the release has its zip and is marked Latest. Published releases are immutable: if one is wrong, release the next patch version.
