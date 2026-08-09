# Privacy and local data

TokenBurn is local-first. It reads AI tooling state from the current Windows user profile and keeps its own settings, cache, history aggregates, and diagnostics on the machine.

## Stays on the device

- TokenBurn settings, layout, provider selections, and notification preferences.
- TokenBurn cache and normalized local usage history.
- Model pricing overrides.
- Diagnostics logs after redaction.
- Provider-owned local history files and databases, which TokenBurn opens for reading only.
- Credentials stored through Windows Credential Manager and DPAPI-backed storage.

## Can leave the device

- Provider refresh requests to the provider endpoint required for live quota data.
- Public model-catalog requests used to refresh pricing, without provider credentials.
- Requests made by the provider's own CLI or login flow. TokenBurn does not handle a browser OAuth response.
- GitHub release metadata when a user or release workflow checks for it. There is no enabled in-app update feed today.

TokenBurn does not send prompts, transcripts, raw session records, local history files, credentials, or diagnostics to a TokenBurn-owned service. The loopback API is local to the machine and is not a cloud endpoint.

## Logging rules

Diagnostics use bounded line-delimited JSON and redact credentials, account identifiers, email addresses, and Windows user paths. Do not add raw prompts, transcripts, session IDs, full local paths, or provider response bodies to logs.

If a bug report needs logs, remove private values first. Screenshots and fixture files must use synthetic data.

Any change to storage, network calls, provider inputs, logging, export behavior, or updater behavior must update this document and the relevant tests.
