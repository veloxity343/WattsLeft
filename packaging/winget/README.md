# winget manifests

What's submitted to [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) for each version, kept here so the next version starts from the last. The package id is `veloxity343.WattsLeft`.

- **Portable zip:** winget unpacks the release zip into a folder of its own. `ArchiveBinariesDependOnPath: true` puts that folder on the PATH, rather than linking to the exe alone, because the exe needs the DLLs beside it.
- **Per version:** copy the last version's folder, then change `PackageVersion`, `ReleaseDate`, the `InstallerUrl`, the `InstallerSha256` (the release's `.sha256` file, in capitals) and the `ReleaseNotesUrl`.
- **Check:** `winget validate --manifest <folder>`, from a Windows path.
- **Submit:** `wingetcreate submit <folder>` (install it with `winget install Microsoft.WingetCreate`). It signs in to GitHub, forks winget-pkgs and opens the pull request; Microsoft's checks and a moderator review it.
