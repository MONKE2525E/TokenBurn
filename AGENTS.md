# TokenBurn agent instructions

TokenBurn is a Windows-first .NET 10 AI usage monitor. It reads supported Codex, Claude Code, Antigravity, and OpenCode local/provider usage sources and exposes normalized quota, token, reset, and estimated-cost data through a Tauri/WebView dashboard, native taskbar/tray surfaces, CLI, and loopback API. The .NET/WPF process is the native Windows host and backend, not the normal main dashboard.

## Important paths

- `UsageMonitor.Core`: models, providers, cache, pricing, secrets, paths, redaction.
- `UsageMonitor.Desktop`: .NET/WPF host, provider refresh orchestration, tray, native taskbar, settings bridge, Win32, and Windows App SDK code. Its `MainWindow` is hidden in the normal hosted runtime.
- `UsageMonitor.LocalApi`: loopback contracts used by the CLI and Tauri host.
- `UsageMonitor.Cli`: one-shot JSON and sanitized diagnostics.
- `UsageMonitor.Tests`: provider fixtures, cache/migration tests, cost tests, API tests, and shell-placement tests.
- `UsageMonitor.TauriPoc`: Rust/Tauri/WebView dashboard, in-popup settings, breakdown views, and popup window boundary. The directory name is historical.

## Workflow and checks

Target feature branches at `dev`. Test and review `dev` before promoting it to `master`. Tag releases from `master` as `TokenBurn-vX.Y.Z`.

Run:

```powershell
dotnet build UsageMonitor.slnx --configuration Release -m:1 /p:BuildInParallel=false /p:UseSharedCompilation=false
dotnet test UsageMonitor.slnx --configuration Release --no-build --no-restore -m:1 /p:BuildInParallel=false /p:UseSharedCompilation=false
node UsageMonitor.TauriPoc\selfcheck.mjs
node UsageMonitor.TauriPoc\interaction-tests.mjs
```

For Tauri changes also run `npm --prefix UsageMonitor.TauriPoc ci`, `npm --prefix UsageMonitor.TauriPoc run build`, `cargo fmt --manifest-path UsageMonitor.TauriPoc\src-tauri\Cargo.toml -- --check`, and `cargo test --manifest-path UsageMonitor.TauriPoc\src-tauri\Cargo.toml`.

## Gotchas

- Never turn unavailable provider data into zeroes.
- Never write to provider-owned databases or load unbounded JSONL histories into one string.
- Keep credentials out of normalized models, logs, fixtures, screenshots, PRs, and test output.
- Do not log prompts, transcripts, raw session records, full paths, or provider response bodies.
- Keep the loopback API loopback-only and preserve cancellation and concurrency limits.
- The Tauri/WebView window is the normal dashboard presentation layer. The WPF/Win32 taskbar and tray boundary remains the reliable native Windows shell layer. Do not casually remove either side for a one-process rewrite.
- The visible taskbar strip is a native layered HWND owned by .NET; WPF renders its small bitmap offscreen. Do not describe that renderer as the main WPF dashboard.
- Preserve settings migrations and safe defaults. Do not casually rewrite provider parsing, pricing, cache, updater/release, or native shell code without tests and docs.
- If behavior, storage, network, supported providers, costs, or release claims change, update the relevant docs.
