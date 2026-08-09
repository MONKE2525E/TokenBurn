# TokenBurn

TokenBurn is a local-first Windows utility for monitoring AI coding usage, quotas, reset times, tokens, and estimated spend from the taskbar, tray, dashboard, CLI, and loopback API.

It reads the local state that supported AI tools already keep on the machine. It does not upload prompts, transcripts, session history, or credentials to a TokenBurn service. It is an independent project inspired by the compact usage surfaces of [OpenUsage](https://github.com/robinebers/openusage), not an official OpenUsage product.

## What works today

- WPF dashboard, notification-area tray, and a native Windows taskbar status surface.
- Tauri presentation popup backed by the existing .NET provider and shell boundary.
- Codex, Claude Code, Antigravity, and OpenCode local usage integrations.
- Honest unsupported states for providers without a stable supported usage source.
- Local 30-day history aggregation and model-aware cost estimates.
- One-shot JSON through `usage-monitor` and a loopback-only API on `127.0.0.1:6736`.
- Windows Credential Manager and DPAPI-backed secret handling, plus redacted local diagnostics.

TokenBurn never fabricates zero usage when a provider is unavailable. Unknown model prices remain unknown instead of being assigned a generic rate. See [Provider integrations](docs/PROVIDERS.md), [Cost methodology](docs/COSTS.md), and [Privacy and local data](docs/PRIVACY.md).

## Project layout

| Path | Responsibility |
| --- | --- |
| `UsageMonitor.Core` | Provider-neutral models, cache, pricing, secrets, paths, and diagnostics |
| `UsageMonitor.LocalApi` | Shared snapshot service and loopback HTTP contract |
| `UsageMonitor.Desktop` | WPF dashboard, tray, taskbar, Windows shell integration, and settings |
| `UsageMonitor.Cli` | One-shot JSON and diagnostic output for scripts and agents |
| `UsageMonitor.Tests` | Unit, fixture, contract, pricing, cache, and Windows shell tests |
| `UsageMonitor.TauriPoc` | Tauri popup presentation host and its Rust control boundary |
| `scripts` and `packaging` | Publish and Inno Setup installer workflows |

The detailed architecture is in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Build and run

Prerequisites: Windows 10 or 11, the .NET 10 SDK, Node.js, Rust and Cargo, and WebView2 for the Tauri popup.

```powershell
dotnet restore UsageMonitor.slnx
dotnet build UsageMonitor.slnx --configuration Release
dotnet test UsageMonitor.slnx --configuration Release
dotnet run --project UsageMonitor.Desktop
```

To validate and build the Tauri presentation host:

```powershell
npm --prefix UsageMonitor.TauriPoc ci
npm --prefix UsageMonitor.TauriPoc run build
node UsageMonitor.TauriPoc\selfcheck.mjs
```

To create a self-contained Windows publish and per-user installer:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\package.ps1 -Version 0.1.39
```

The installer is written to `artifacts\installer`. It creates Start menu entries, supports uninstall, and offers an opt-in per-user PATH entry for the bundled CLI. Release signing and an in-app update feed are not enabled yet.

## Documentation

Start with [docs/README.md](docs/README.md). The most useful guides are:

- [Installation and setup](docs/INSTALLATION.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Testing](docs/TESTING.md)
- [Contributing and branch flow](docs/CONTRIBUTING.md)
- [Release process](docs/RELEASE.md)
- [Troubleshooting](docs/TROUBLESHOOTING.md)
- [Security policy](SECURITY.md)

## Privacy

Settings, cache, local usage history, pricing overrides, and diagnostics stay in Windows application-data directories. Credentials are read from supported local stores and are not written to the repository, cache, logs, or API responses. Telemetry and crash uploads are disabled by default. Read [docs/PRIVACY.md](docs/PRIVACY.md) for the data flow and deletion boundaries.

## License and attribution

TokenBurn is licensed under the MIT License. See [LICENSE](LICENSE). The `upstream-openusage` directory is retained for local reference and attribution boundaries. TokenBurn does not use OpenUsage branding or logo assets.
