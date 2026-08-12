# Security policy

TokenBurn reads local AI tooling state and may encounter credentials, account identifiers, prompts, transcripts, session history, provider responses, and full Windows paths. Treat all of those as sensitive.

## Reporting a vulnerability

Please use the repository's [private GitHub Security Advisory form](../../security/advisories/new) when it is available. Do not open a public issue for an undisclosed vulnerability. If private reporting is unavailable, open a minimal issue asking for a private reporting channel without including exploit details.

Include only sanitized information:

- affected version or commit
- Windows version and affected boundary
- concise reproduction using fake data
- expected and observed impact
- whether credentials, local usage, logs, or release artifacts are exposed

Do not include real API keys, tokens, prompts, transcripts, emails, account IDs, full local paths, or unredacted logs.

## Security boundaries

- Secrets belong in Windows Credential Manager or DPAPI-backed storage, never in settings JSON, cache files, logs, fixtures, PR comments, or release assets.
- Provider-owned JSONL and SQLite histories are read within the narrowest supported scope. OpenCode and Codex databases are read-only inputs.
- Diagnostics use redaction and bounded file size. New logs must use metadata rather than raw provider responses or user content.
- The loopback API must remain bound to loopback unless a reviewed authentication and threat model are added. Every loopback surface (6736 usage API, 6737 popup control, 6738 settings/refresh server) validates the Host header (DNS-rebinding defense) and rejects browser requests whose Origin is not an exact allowlisted app origin (the embedded Tauri/WebView2 origins and the local Vite dev origin). CORS reflects only allowlisted origins, never a wildcard. Same-user native processes remain trusted because they can already read the user's files; a process-secret scheme would not raise that bar.
- Side-effectful loopback routes (forced refreshes, settings writes, popup show/hide) require either an allowlisted browser Origin or the `X-TokenBurn-Client: 1` native-client marker header. This closes the `<img>`/`<script>`/`<link>` hole: those browser primitives issue requests without an Origin header, so the origin gate alone cannot see them, but browsers cannot attach a custom header without a CORS preflight the origin gate rejects. The marker is a browser-context discriminator, **not** a secret and **not** an authentication boundary: any same-user process can send it, and same-user processes are already trusted. Never treat it as a credential, and never use it to authorize anything a hostile webpage could not also do.
- Provider calls must have cancellation, bounded work, and explicit retry behavior. A refresh must not become an unbounded paid API loop.
- GitHub Actions use read-only permissions by default. Release publishing is limited to the release job.
- Dependabot is configured for GitHub Actions, NuGet, npm, and Cargo updates. Review dependency PRs for runtime, packaging, and privacy impact before merging.
- AI review runs from `pull_request_target` only because it needs to post review comments. It never checks out or executes PR code, uses exact commit SHAs, quarantines the review worktree, overwrites review rules from the trusted base commit, and treats PR text and diffs as data.

See [Privacy and local data](docs/PRIVACY.md) for the user-facing data map.
