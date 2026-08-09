# Testing TokenBurn

## Fast local pass

Run these commands from the repository root on Windows:

```powershell
dotnet build UsageMonitor.slnx --configuration Release -m:1 /p:BuildInParallel=false /p:UseSharedCompilation=false
dotnet test UsageMonitor.slnx --configuration Release --no-build --no-restore -m:1 /p:BuildInParallel=false /p:UseSharedCompilation=false
node UsageMonitor.TauriPoc\selfcheck.mjs
```

The serialized MSBuild options are intentional. Windows App SDK resource generation can contend when the desktop and test projects build in parallel.

## Tauri checks

When changing the Tauri popup or Rust control boundary:

```powershell
npm --prefix UsageMonitor.TauriPoc ci
npm --prefix UsageMonitor.TauriPoc run build
cargo fmt --manifest-path UsageMonitor.TauriPoc\src-tauri\Cargo.toml -- --check
cargo test --manifest-path UsageMonitor.TauriPoc\src-tauri\Cargo.toml
node UsageMonitor.TauriPoc\selfcheck.mjs
```

For visual or interaction changes, run the app on Windows and use Playwright or a manual dual-display check when the change affects popup placement, taskbar behavior, tray behavior, DPI, or focus.

## Test coverage expectations

- Provider parser changes need fixtures and failure-state coverage.
- Usage and cost changes need exact aggregation tests, including unknown pricing and empty data.
- Cache, migration, and local storage changes need round-trip and failure-path tests.
- Native shell changes need placement, DPI, tray, taskbar, and fallback tests where applicable.
- API changes need contract tests and loopback binding tests.
- Never use real credentials, real prompts, real transcripts, or personal paths in tests.
