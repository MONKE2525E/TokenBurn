# Cost calculation methodology

TokenBurn shows two different kinds of usage information and labels estimates so they are not confused with provider billing.

## Sources and precedence

1. Known model pricing from the local catalog is applied to token counts when a price is available.
2. A provider-reported cost is used only when the model has no known price (OpenCode) or the provider itself reports the cost (Claude).
3. Explicit local Claude model overrides can add pricing for aliases not in the built-in catalog.
4. If a model cannot be priced safely, its token usage remains visible while its cost is marked unknown.

The normalized model records the cost basis, pricing basis, estimated flag, token categories, and daily breakdown. UI totals are aggregates of those normalized rows.

## OpenCode

OpenCode persists its own per-message cost, but that local estimate has been observed to be exactly half the current market rate for some models (notably `opencode-go/deepseek-v4-flash`), under-reporting spend by about 2x against the OpenCode billing dashboard. TokenBurn therefore prices OpenCode history and the Session/Weekly/Monthly Go meters from the model catalog (the bundled rate or the live catalog) rather than trusting the persisted cost. OpenCode Go rates are pinned in `CachedModelCatalog` so the live OpenRouter catalog cannot shadow them with a different cache price. The bundled rate for `deepseek-v4-flash` is the official OpenCode Go off-peak rate ($0.22 input / $0.007 cached read / $0.66 output per million tokens). DeepSeek bills peak hours (01:00-04:00 and 06:00-10:00 UTC) at double the off-peak rate, so the estimate is a conservative floor that can understate spend for usage concentrated in those hours. Free-routed models (names ending in `-free`) are always priced at zero. A persisted cost is kept only when a row has no token components to price or the model is unknown.

## What the totals mean

- The dashboard's local spend periods aggregate history that is available on the machine.
- The default history window for the spend surface is 30 days.
- Cost-per-million-token views use total cost divided by total tokens. They are blended rates, not a sum of provider rates.
- Cache reads, cache creation, output, reasoning, and uncached input tokens remain distinct in the breakdown when the source provides them.
- Unknown or unavailable data is not converted into a zero-cost success.

These values are estimates unless the provider itself reported the cost. They are not invoices, account balances, or a replacement for provider billing pages.
