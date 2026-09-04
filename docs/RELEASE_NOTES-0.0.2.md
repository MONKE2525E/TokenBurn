# TokenBurn 0.0.2

Release status: prepared, not tagged.

## Highlights

- Added Grok Build and OpenCode history to the dashboard, breakdown, charts, and spend totals.
- Added banked-reset reporting for Codex and supported Grok responses.
- Replaced the usage-history selector with an expandable provider card that shows Today, Yesterday, and Last 30 Days together.
- Kept Antigravity OAuth client credentials outside TokenBurn. Token-only credentials continue using their access token until it expires and need provider client settings for refresh.
- Hardened startup refresh recovery and kept stale provider data visibly separate from fresh data.

## Verification

- 368 .NET tests passed.
- 37 popup interaction tests passed.
- 31 Rust tests passed; 3 clipboard tests remain intentionally ignored.
- Tauri self-check and debug build passed.
- Live Antigravity forced refresh returned fresh Pro quota data with no errors.

After this PR lands on `master`, create the annotated tag `TokenBurn-v0.0.2` and push it to start the Windows release workflow.
