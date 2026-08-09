# TokenBurn

TokenBurn is a Windows-native usage dashboard for AI coding subscriptions. It is an independent Windows derivative inspired by the behavior of [OpenUsage](https://github.com/robinebers/openusage). It is not an official OpenUsage product and does not use OpenUsage branding or logo assets.

## Current status

The current Windows build is packaged as `0.1.39`:

- `UsageMonitor.Core` contains provider-neutral models, cache, settings, secure-store abstractions, and diagnostics.
- `UsageMonitor.Desktop` contains the WPF dashboard, notification-area tray, and taskbar status strip.
- `UsageMonitor.Cli` exposes one-shot JSON for scripts and agents.
- `UsageMonitor.LocalApi` exposes the loopback usage contract on `127.0.0.1:6736`.
- `UsageMonitor.Tests` contains unit, fixture, contract, and shell-placement tests.
- `UsageMonitor.TauriPoc` is the Tauri presentation host. It reuses the loopback API and leaves the
  proven WPF/Win32 taskbar strip available as the native shell boundary.

The dashboard also surfaces redacted local 30-day spend totals when Codex or Claude Code session history contains enough token data for an estimate. Those estimates stay on the machine and are marked as local/estimated in the normalized model.

Large Codex and Claude JSONL histories are streamed during refresh instead of loaded as one
giant string. This keeps local spend collection bounded on real Windows workstations. If Claude's
OAuth session expires, the Claude card keeps its local history and offers an explicit `claude auth
login` action; TokenBurn never handles the browser OAuth response itself.

The first-run surface is the compact OpenUsage-style taskbar status strip plus the notification-area tray. Because Windows has no public embedded-widget API, the strip is isolated and can fall back to the supported native taskbar button and tray. The taskbar icon carries up to four selected metrics, the tooltip contains exact values and reset times, and a normal taskbar click restores the dashboard.

The native taskbar button follows Windows taskbar ownership and cannot be forced onto a specific monitor. Monitor selection applies only to the experimental status strip. The app never auto-pins itself to the taskbar.

### Tauri presentation boundary

The Tauri slice has been validated on the Windows dual-display machine against the compact OpenUsage
reference: it opens as a frameless 320x800 logical-pixel tray popup, anchors from the clicked tray or
taskbar coordinates, renders live Codex and Antigravity limits, preserves Claude and Codex local
history during provider rate limits, animates quota bars and the spend ring, and has no taskbar button
(`WS_EX_TOOLWINDOW`). Options now hands off to the existing WPF settings/customization surface over
a loopback-only control channel. The native taskbar strip remains in the .NET process because its
Explorer attachment is the reliable Windows-specific part of the shell. This is a deliberate hybrid
boundary, not a blind rewrite of provider or shell code.

OpenUsage parity is intentionally staged. The macOS-only AppKit, Liquid Glass, iCloud, Keychain, and Sparkle pieces are not ported. WSL probing is deferred. Cursor, Copilot, Devin, and Grok are registered with honest capability detection; Grok has a live xAI model probe, while the others remain unsupported until a stable non-scraping usage source is available. The Windows build now has provider/metric customization, spend ring periods, compact trend bars, reset countdowns, cached model pricing, screen-share exclusion, and opt-in quota alerts. Model-detail hover cards, drag reorder, global shortcut recording, signed updater feed, and cloud sync remain follow-up work rather than pretending to be complete.

## Build

```powershell
dotnet build UsageMonitor.slnx
dotnet test UsageMonitor.slnx
dotnet run --project UsageMonitor.Desktop
```

To produce a self-contained Windows build and per-user installer:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\package.ps1 -Version 0.1.39
```

The installer is written to `artifacts\installer`. It creates Start menu entries, supports uninstall, and offers an opt-in per-user PATH entry for the bundled `usage-monitor` CLI. Release signing and an update feed are intentionally not enabled until a real Windows signing certificate and release endpoint are available.

The first release targets Windows-native Codex, Claude Code, and Antigravity integrations. The monitor reads the credentials and local history those tools already maintain. A cached public model catalog can refresh pricing without sending provider credentials; local fallback rates remain available offline.

Codex and Claude Code have live Windows-native readers and refresh paths. Claude also preserves local session spend when Anthropic rejects an expired OAuth refresh, instead of showing fabricated zeroes. Antigravity has a live Windows quota reader using the existing `gemini:antigravity` Credential Manager target and the Gemini CLI OAuth file fallback, with merged Gemini/non-Gemini five-hour and weekly pools. Grok can probe xAI models with `XAI_API_KEY`; Cursor, Copilot, and Devin report unsupported rather than scraping private dashboards or inventing quotas. Unsupported providers report unavailable, never fake zero usage.

Custom Claude Code model aliases can be priced explicitly in `%LOCALAPPDATA%\UsageMonitor\Pricing\model-overrides.json`. Unresolved models still show token usage, but their cost is left unknown instead of using a generic provider price.

## Privacy

Telemetry and crash uploads are disabled by default. UsageMonitor stores settings and logs under the Windows application-data directories and uses Windows Credential Manager/DPAPI for user-entered secrets. The local API listens only on loopback.

## License and upstream attribution

This project is licensed under the MIT terms that apply to its original source components. See `upstream-openusage/LICENSE` and `upstream-openusage/TRADEMARK.md` for the upstream license and branding policy. CodeZeno's Claude Code Usage Monitor is used only as a behavioral reference for the taskbar integration; its source is not copied.
