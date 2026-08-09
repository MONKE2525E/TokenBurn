# Troubleshooting

## Build fails with a Windows App SDK resource lock

Stop stale MSBuild nodes and rerun the serialized commands:

```powershell
dotnet build-server shutdown
dotnet build UsageMonitor.slnx --configuration Release -m:1 /p:BuildInParallel=false /p:UseSharedCompilation=false
```

## A provider shows unavailable or stale data

TokenBurn does not convert provider failures into zero usage. Check the provider's own login state, wait for rate limits to clear, then refresh. Codex and Claude local history can remain visible when live usage calls fail.

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
