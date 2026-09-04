# Troubleshooting

## Build fails with a Windows App SDK resource lock

Stop stale MSBuild nodes and rerun the serialized commands:

```powershell
dotnet build-server shutdown
dotnet build UsageMonitor.slnx --configuration Release -m:1 /p:BuildInParallel=false /p:UseSharedCompilation=false
```

## A provider shows unavailable or stale data

TokenBurn does not convert provider failures into zero usage. Check the provider's own login state, wait for rate limits to clear, then refresh. Codex and Claude local history can remain visible when live usage calls fail.

A manual Refresh requests a live provider read. If that read fails, TokenBurn keeps the last-good/cached values and carries the error as a badge next to those bars instead of removing the provider from the taskbar strip. Scheduled reads still use the stale-while-revalidate cache.

## Refresh stays on loading

Provider refreshes have a 30-second per-provider deadline and the desktop host has a 45-second deadline for the complete refresh batch. A provider or local history scanner that does not return is reported as unavailable after the provider deadline; if the whole batch still does not settle, the host logs a correlated timeout, clears the loading state, keeps the last-good values, and retries automatically after one minute. If the native host is restarting, the popup also clears stale loading state on its next failed status poll; close/reopen should no longer be required to recover the dashboard.

Historical spend is now scanned incrementally. Unchanged Codex, Claude, and Grok session files (same size and last-write time) are not re-read on later refreshes; their previously computed daily totals are reused from `%LOCALAPPDATA%\UsageMonitor\Cache\history-*.json` and only changed files are re-parsed. OpenCode reads its local SQLite history read-only. The index is rebuilt automatically when the pricing catalog changes, so updated model rates still apply to old records.

After a quota reset the taskbar updates on the next scheduled refresh cycle (within a few minutes). Resets no longer force a full network + history refresh of every provider, which is what previously caused the multi-second refreshes and CPU spikes.

## Claude asks for login

Run `claude auth login` through the Claude Code installation, then refresh TokenBurn. TokenBurn does not capture or proxy the browser OAuth response.

## Local spend is unknown

Token counts can be available even when a model price is not. Add a correct Claude model alias to `%LOCALAPPDATA%\UsageMonitor\Pricing\model-overrides.json`, or treat the displayed token total as the useful value. Do not insert a generic rate just to make the dollar total non-empty.

## The Tauri popup does not open

Build the Tauri presentation host and confirm the .NET host can find `tokenburn-desktop.exe` beside
its published output. The taskbar and tray can remain available when the companion is missing, but
the current normal dashboard is Tauri/WebView, not the legacy WPF window. If the companion exits,
the .NET host records the exit and retries a few times. A tray or taskbar click also starts a fresh
attempt after the retry limit, so restarting the whole monitor should not be necessary.

## Taskbar or popup placement is wrong

Check the selected monitor, taskbar edge, DPI scale, and whether Explorer restarted. TokenBurn keeps the taskbar button under Windows taskbar ownership; the experimental status strip has separate monitor and placement settings.

## Reporting a problem

Include Windows version, TokenBurn version, provider name, expected behavior, observed behavior, and sanitized test output. Do not include API keys, email addresses, raw prompts, transcripts, full local paths, session IDs, or unredacted logs.

## Sharing logs for debugging

Open Settings → Diagnostics and choose **Copy logs**. That copies the current session's redacted log tail plus a client-state snapshot (settings, per-provider status and errors, refresh status, catalog and cache state, app/OS/runtime versions) to the clipboard so it can be pasted into an assistant. The bundle is already scrubbed; no credentials or provider bodies are included. The full redacted log lives at `%LOCALAPPDATA%\UsageMonitor\Logs\usage-monitor.log`. Shell-placement issues can additionally be traced through the bounded `taskbar-strip.log` beside it, and every refresh in the log carries a shared `refreshId` from start to completion.
