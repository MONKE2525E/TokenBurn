# Installation and setup

## Use a release

Download the latest Windows installer from the repository's Releases page, run it for the current Windows user, and launch TokenBurn from the Start menu. The installer can optionally add the bundled `usage-monitor` CLI directory to the user PATH.

Releases are currently unsigned and do not use an automatic update feed. Windows SmartScreen may show an unknown-publisher warning. Verify the release source and the published SHA-256 file before installing if the warning matters to you.

## Build from source

Install Windows 10 or 11, the .NET 10 SDK, Node.js, Rust and Cargo, WebView2 for the Tauri popup, and Inno Setup 6 when building the installer.

```powershell
git clone <repository-url>
cd TokenBurn
dotnet restore UsageMonitor.slnx
dotnet build UsageMonitor.slnx --configuration Release
dotnet run --project UsageMonitor.Desktop
```

Build the Tauri presentation host separately:

```powershell
npm --prefix UsageMonitor.TauriPoc ci
npm --prefix UsageMonitor.TauriPoc run build
node UsageMonitor.TauriPoc\selfcheck.mjs
```

## First run

TokenBurn discovers supported provider state from the local Windows account. It does not ask TokenBurn users to paste provider credentials into the repository or into logs. If a provider is signed out, use that provider's own CLI or application login flow, then refresh TokenBurn.

For model aliases that are present in Claude Code history but absent from the catalog, add explicit local overrides at `%LOCALAPPDATA%\UsageMonitor\Pricing\model-overrides.json`. Unresolved model prices remain unknown.
