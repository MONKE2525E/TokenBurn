# Provider integrations

TokenBurn separates provider capability from provider identity. Every provider returns a normalized snapshot and an explicit state when data is missing, stale, rate limited, unsupported, or unparsable.

| Provider | Current source | Current behavior |
| --- | --- | --- |
| Codex | Local session JSONL and Codex logs SQLite, plus the supported usage endpoint | Live quota data and local history when token data is available |
| Claude Code | Claude OAuth state, supported usage endpoint, and local session JSONL | Live quota data and local history; expired OAuth preserves local history and shows a login action |
| Antigravity | Windows Credential Manager target `gemini:antigravity` and Gemini CLI OAuth fallback | Live five-hour and weekly quota pools with merged provider data |
| OpenCode | OpenCode-owned local SQLite database | Read-only local history and cost data; no API key is required for that local source. History is priced from the model catalog because OpenCode's persisted cost can be half the market rate for some models. `opencode-go` and `opencode` rows share one canonical OpenCode identity |
| Grok | Grok Build (`grok` CLI) local login state in `~/.grok/auth.json` | Live Grok Build coding quota (weekly/monthly limit as a percentage plus the period reset time, prepaid credits, and on-demand spend) fetched with the CLI's own session token — no xAI API key required. Requires `grok login` to have run once |
| Cursor | Registered provider descriptor | Unsupported until a stable non-scraping source exists |
| Copilot | Registered provider descriptor | Unsupported until a stable non-scraping source exists |
| Devin | Registered provider descriptor | Unsupported until a stable non-scraping source exists |

Legacy billing-key adapters remain in the source for compatibility, but they are not part of the default Windows product catalog unless explicitly wired into it.

## Provider implementation rules

- Use the provider's existing credential or local history boundary. Do not scrape private dashboards as a shortcut.
- Keep access tokens and API keys out of `ProviderSnapshot`, logs, fixtures, test output, and the loopback response.
- Use bounded HTTP timeouts and cancellation. Do not introduce retry loops without a clear retryable-error gate and backoff.
- Preserve last-good local history when a live provider call fails.
- Bucket local-history totals by the Windows local calendar day (not UTC) so the dashboard's local "Today"/"Yesterday" selectors and the breakdown chart match the user's own day boundary.
- Add fixture coverage for new response shapes and parser edge cases.

Antigravity refresh-token rotation reads OAuth client credentials from the existing local OAuth envelope when the provider supplies them. Installations without those fields can provide `TOKENBURN_GOOGLE_CLIENT_ID` and `TOKENBURN_GOOGLE_CLIENT_SECRET` in the process environment. TokenBurn does not ship client credentials in source or write them back to disk. When the stored access token expires and no client credentials are available to rotate it, Antigravity reports a re-authentication state: the dashboard keeps the last-known-good quota bars, surfaces an `Antigravity needs re-authentication` toast once per failure episode, and shows a **Run agy to sign in** action that opens the Antigravity CLI so the user can refresh their session. After `agy` updates the credential, the next refresh picks up the fresh access token.
