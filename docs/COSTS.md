# Cost calculation methodology

TokenBurn shows two different kinds of usage information and labels estimates so they are not confused with provider billing.

## Sources and precedence

1. A provider-reported cost is used when the source supplies one.
2. Otherwise, known model pricing from the local catalog is applied to token counts.
3. Explicit local Claude model overrides can add pricing for aliases not in the built-in catalog.
4. If a model cannot be priced safely, its token usage remains visible while its cost is marked unknown.

The normalized model records the cost basis, pricing basis, estimated flag, token categories, and daily breakdown. UI totals are aggregates of those normalized rows.

## What the totals mean

- The dashboard's local spend periods aggregate history that is available on the machine.
- The default history window for the spend surface is 30 days.
- Cost-per-million-token views use total cost divided by total tokens. They are blended rates, not a sum of provider rates.
- Cache reads, cache creation, output, reasoning, and uncached input tokens remain distinct in the breakdown when the source provides them.
- Unknown or unavailable data is not converted into a zero-cost success.

These values are estimates unless the provider itself reported the cost. They are not invoices, account balances, or a replacement for provider billing pages.
