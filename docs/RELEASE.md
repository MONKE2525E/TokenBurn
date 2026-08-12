# TokenBurn release process

Releases are made from `master` after the corresponding changes have landed through `dev` and the stable branch has passed CI.

## Manual release steps

1. Merge the tested `dev` state into `master`.
2. Confirm the version and release notes. The installer version is passed to `scripts/package.ps1` and embedded in the Inno Setup filename.
3. Create an annotated tag from the stable commit using `TokenBurn-vX.Y.Z`.
4. Push the tag to GitHub.
5. The release workflow builds the Windows package, writes SHA-256 checksums, uploads the installer artifact, and publishes a GitHub release for version tags.
6. Verify the installer, CLI, Start menu entry, uninstall path, notification support, and provider fallback behavior on a clean Windows user profile.

Example:

```powershell
git switch master
git pull --ff-only origin master
git tag -a TokenBurn-v0.0.1 -m "TokenBurn 0.0.1"
git push origin TokenBurn-v0.0.1
```

## Packaging notes

- `scripts/publish.ps1` publishes the desktop and CLI binaries for `win-x64` or `win-arm64`.
- `scripts/package.ps1` invokes Inno Setup and writes to `artifacts\installer`.
- The installer can register the Windows App Runtime notification package and can add the CLI directory to the current user's PATH when selected.
- The Tauri dashboard binary is copied when the Tauri build exists. The .NET/WPF taskbar and tray
  host remains the supported native shell boundary, but it is not a replacement dashboard.
- Release signing and automatic in-app updating are not enabled. Do not describe an artifact as signed or auto-updating until that is implemented and verified.

Never put credentials, personal test data, raw logs, or private provider histories in release assets.
