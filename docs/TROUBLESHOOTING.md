# Troubleshooting

## Build fails with a Windows App SDK resource lock

Stop stale MSBuild nodes and rerun the serialized commands:

```powershell
dotnet build-server shutdown
dotnet build UsageMonitor.slnx --configuration Release -m:1 /p:BuildInParallel=false /p:UseSharedCompilation=false
```

## A provider shows unavailable or stale data

TokenBurn does not convert provider failures into zero usage. Check the provider's own login state, wait for rate limits to clear, then refresh. Codex and Claude local history can remain visible when live usage calls fail.

A manual Refresh uses the same stale-while-revalidate pipeline as the scheduler: it serves the last-good/cached values immediately and only re-reads a provider after its five-minute cache expires, so a failed authentication attempt never blanks a provider's last-known-good bars. The error is carried as a badge next to those bars instead of removing the provider from the taskbar strip.

Historical spend is now scanned incrementally. Unchanged Codex/Claude session files (same size and last-write time) are not re-read on later refreshes; their previously computed daily totals are reused from `%LOCALAPPDATA%\UsageMonitor\Cache\history-*.json` and only changed files are re-parsed. The index is rebuilt automatically when the pricing catalog changes, so updated model rates still apply to old records.

After a quota reset the taskbar updates on the next scheduled refresh cycle (within a few minutes). Resets no longer force a full network + history refresh of every provider, which is what previously caused the multi-second refreshes and CPU spikes.

## Claude asks for login

Run `claude auth login` through the Claude Code installation, then refresh TokenBurn. TokenBurn does not capture or proxy the browser OAuth response.

## Local spend is unknown

Token counts can be available even when a model price is not. Add a correct Claude model alias to `%LOCALAPPDATA%\UsageMonitor\Pricing\model-overrides.json`, or treat the displayed token total as the useful value. Do not insert a generic rate just to make the dollar total non-empty.

## The Tauri popup does not open

Build the Tauri presentation host and confirm the .NET host can find `tokenburn-desktop.exe` beside
its published output. The taskbar and tray can remain available when the companion is missing, but
the current normal dashboard is Tauri/WebView, not the legacy WPF window.

## Taskbar or popup placement is wrong

Check the selected monitor, taskbar edge, DPI scale, and whether Explorer restarted. TokenBurn keeps the taskbar button under Windows taskbar ownership; the experimental status strip has separate monitor and placement settings.

## Reporting a problem

Include Windows version, TokenBurn version, provider name, expected behavior, observed behavior, and sanitized test output. Do not include API keys, email addresses, raw prompts, transcripts, full local paths, session IDs, or unredacted logs.

## Sharing logs for debugging

Open Settings → Diagnostics and choose **Copy logs**. That copies the current session's redacted log tail plus a client-state snapshot (settings, per-provider status and errors, refresh status, catalog and cache state, app/OS/runtime versions) to the clipboard so it can be pasted into an assistant. The bundle is already scrubbed; no credentials or provider bodies are included. The full redacted log lives at `%LOCALAPPDATA%\UsageMonitor\Logs\usage-monitor.log`. Shell-placement issues can additionally be traced through the bounded `taskbar-strip.log` beside it, and every refresh in the log carries a shared `refreshId` from start to completion.
