# TokenBurn

TokenBurn started as a personal Windows utility. I wanted a quick way to glance at AI coding usage and quota information from the taskbar, and the tools I found did not work the way I wanted. So I built this one.

It is a local-first Windows AI usage monitor for supported Codex, Claude Code, Antigravity, OpenCode, and related provider sources. It can surface quotas, reset times, token usage, estimated cost, caching information, and local usage history where the provider exposes enough data to calculate them honestly.

TokenBurn is intentionally more Windows-native than a normal web dashboard. The WPF shell owns the taskbar, tray, settings, and Windows integration. The optional Tauri process provides a compact popup. The project has no TokenBurn cloud service, accounts, telemetry pipeline, or hosted backend.

## What works today

- WPF dashboard, notification-area tray, and native Windows taskbar status surface.
- Optional Tauri popup backed by the existing .NET provider and shell boundary.
- Local/provider integrations for Codex, Claude Code, Antigravity, and OpenCode.
- Honest unavailable and unsupported states instead of fabricated zero usage.
- Local 30-day history aggregation and model-aware cost estimates where pricing is known.
- One-shot JSON through `usage-monitor` and a loopback-only API on `127.0.0.1:6736`.
- Windows Credential Manager and DPAPI-backed secret handling, plus redacted local diagnostics.

Some refreshes contact the relevant provider using credentials already available on the machine. TokenBurn does not upload prompts, transcripts, session history, or credentials to a TokenBurn service. Unknown model prices remain unknown instead of being assigned a generic rate. See [provider integrations](docs/PROVIDERS.md), [cost methodology](docs/COSTS.md), and [privacy and local data](docs/PRIVACY.md).

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

## Credits and inspiration

TokenBurn is an independent project. The projects below do not endorse, sponsor, maintain, or have any affiliation with TokenBurn.

### OpenUsage

[OpenUsage](https://github.com/robinebers/openusage) was the biggest conceptual and visual influence. It established the general product idea of making AI usage, quotas, resets, providers, and token or cost information easy to see in a dedicated desktop utility.

TokenBurn took inspiration from OpenUsage's quota and usage presentation, provider cards, usage indicators, visual organization, and compact at-a-glance surface. The Windows experience evolved differently around persistent taskbar information, popup behavior, provider switching, detailed usage analysis, and native shell integration. TokenBurn is not a Windows port of OpenUsage.

This repository also contains a limited amount of OpenUsage-derived material: the `openusage.limits.v1` compatibility schema, adapted near-full quota-bar geometry, and provider icon SVG assets. Those items are documented in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

### Pane

[Pane](https://github.com/ItsJazii/pane) was a useful Windows product reference. It helped validate that a local tray utility for AI subscription limits was a sensible category and provided a practical reference for how a Windows usage monitor could feel.

TokenBurn was built around a different set of priorities: persistent taskbar visibility, compact quota information, popup interactions, detailed provider and model analysis, and a different visual design. No Pane code or assets were copied into TokenBurn.

### T3 Code

[T3 Code](https://github.com/pingdotgg/t3code) was the primary visual reference for TokenBurn's detailed usage dashboard. The influence is direct: TokenBurn's usage page was heavily inspired by T3 Code's usage screen, including large headline usage and cost metrics, time-series usage and cost graphs, provider breakdowns, model breakdowns, cache-related statistics, and the overall information hierarchy.

TokenBurn adds its own provider support, local data model, Windows behavior, metrics, and cost methodology on top of that reference. No T3 Code code or assets were copied into TokenBurn.

## Privacy

Settings, cache, local usage history, pricing overrides, and diagnostics stay in Windows application-data directories. Credentials are read from supported local stores and are not written to the repository, cache, logs, or API responses. Read [docs/PRIVACY.md](docs/PRIVACY.md) for the data flow and deletion boundaries.

## License

TokenBurn's original code is licensed under the [MIT License](LICENSE). Third-party material that requires a separate notice is listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
