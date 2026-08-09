# Provider integrations

TokenBurn separates provider capability from provider identity. Every provider returns a normalized snapshot and an explicit state when data is missing, stale, rate limited, unsupported, or unparsable.

| Provider | Current source | Current behavior |
| --- | --- | --- |
| Codex | Local session JSONL and Codex logs SQLite, plus the supported usage endpoint | Live quota data and local history when token data is available |
| Claude Code | Claude OAuth state, supported usage endpoint, and local session JSONL | Live quota data and local history; expired OAuth preserves local history and shows a login action |
| Antigravity | Windows Credential Manager target `gemini:antigravity` and Gemini CLI OAuth fallback | Live five-hour and weekly quota pools with merged provider data |
| OpenCode | OpenCode-owned local SQLite database | Read-only local history and cost data; no API key is required for that local source |
| Grok | xAI model probe using `XAI_API_KEY` when present | Capability probe only, not a fabricated subscription quota |
| Cursor | Registered provider descriptor | Unsupported until a stable non-scraping source exists |
| Copilot | Registered provider descriptor | Unsupported until a stable non-scraping source exists |
| Devin | Registered provider descriptor | Unsupported until a stable non-scraping source exists |

Legacy billing-key adapters remain in the source for compatibility, but they are not part of the default Windows product catalog unless explicitly wired into it.

## Provider implementation rules

- Use the provider's existing credential or local history boundary. Do not scrape private dashboards as a shortcut.
- Keep access tokens and API keys out of `ProviderSnapshot`, logs, fixtures, test output, and the loopback response.
- Use bounded HTTP timeouts and cancellation. Do not introduce retry loops without a clear retryable-error gate and backoff.
- Preserve last-good local history when a live provider call fails.
- Add fixture coverage for new response shapes and parser edge cases.
