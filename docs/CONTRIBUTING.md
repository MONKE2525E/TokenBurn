# Contributing to TokenBurn

Keep changes focused, evidence-based, Windows-aware, and honest about provider capability.

## Branch flow

- `dev` is the normal integration branch.
- `master` is the stable release branch.
- Feature and fix branches target `dev`.
- `dev` is tested and reviewed before it is promoted to `master`.
- Release tags are created from the stabilized `master` commit.

Use a pull request for provider behavior, cost calculations, privacy boundaries, migrations, release packaging, native shell behavior, or any broad or risky change. Direct pushes to protected branches should be exceptional.

## Before opening a PR

1. Read [`../AGENTS.md`](../AGENTS.md) and [Architecture](ARCHITECTURE.md).
2. Check [Roadmap](ROADMAP.md) so planned work is not duplicated accidentally.
3. Run the relevant .NET, Tauri, and self-check commands from [Testing](TESTING.md).
4. Review the diff for secrets, personal data, raw logs, and accidental provider writes.
5. Update docs when behavior, data flow, supported providers, or release behavior changes.

## Pull requests

The PR template requires a Thinking Path, What Changed, Verification, Risks, Model Used, and checklist. Explain the path from the TokenBurn product to the affected subsystem, not just the final line edit. State which AI model assisted with the work, or say that the change was human-authored.

## Development rules

- Do not replace the native WPF/Win32 taskbar boundary casually.
- Do not turn unsupported providers into fake zeroes or scrape private dashboards without an explicit design decision.
- Do not load unbounded provider histories into one string.
- Do not write to provider-owned databases.
- Keep secrets in OS-backed stores and keep raw private content out of logs and fixtures.
- Keep new runtime dependencies justified, pinned through lock files, and reviewed for supply-chain and memory impact.
