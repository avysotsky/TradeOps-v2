# TO-V2-08 — Strict IBKR Web API order response replay (offline)

**Stack:** Draft PR #9 / TO-V2-07 on top of Draft PR #8 / TO-V2-06.
**Zero broker transport**; no authenticated session, HTTP requests,
real account IDs, credentials, network dependency, or actual trading.

## Official Web API response formats (verified 2026-10-22)

Source material:

- https://www.interactivebrokers.com/docs/web-api/v1/endpoints/orders/place-order
- https://www.interactivebrokers.com/docs/web-api/trading/orders/order-reply-messages

IBKR documents a single-order response as either:

1. An order acknowledgement (`order_id`, `order_status`), shown as an
   array containing one object, and in reply docs as a single object.
2. A precaution prompt (`id`, `message` array, optional
   `isSuppressed: false` and `messageIds`) that **requires a separate
   explicit confirmation** before the order can be considered acknowledged.
   Prompt `id` is the reply identifier. Category `messageIds` are **not**
   reply identifiers.
3. A broker rejection as a JSON `error` **even when HTTP status is 200**.

The official guide explicitly warns against sending another order while a
pending reply remains unresolved. The existing application deliberately
does not implement any of the referenced order placement or reply POSTs.

## TO-V2-08 code

- `IbkrOfflineOrderResponseParser` recognizes these three synthetic shapes
  and gives a typed outcome: `Acknowledged`, `ReplyRequired`, `Rejected`.
- Accepts either one object or a single-item array, never a multi-order
  batch. Rejects duplicate top-level fields, ambiguous combinations,
  missing or malformed identifiers, empty warning texts, invalid
  suppression flag, and statuses outside the deliberately narrow
  `Submitted` / `PreSubmitted` / `PendingSubmit` allowlist.
- Rejects abnormal identifiers (no path punctuation), unknown values of
  encryption marker, oversized response, and invalid JSON.
- Warning/error text is validated **but not stored** in returned results,
  which reduces risk of private data appearing in replay trace output.
- `IbkrOfflineOrderResponseReplay` converts **locally injected** response
  strings to events for the TO-V2-07 `IbkrOfflineOrderLifecycle` reducer.
  An explicit `ReplyConfirmed` test event is required before processing
  a follow-on confirmation response. Parsing a warning does NOT confirm it.
- The parser never builds a URL or sends a broker request. It cannot
  auto-accept a warning or suppress prompts. `IbkrPaperExchangeClient`
  and its trading capability catalog remain unchanged/read-only.

## Offline verification

```bash
dotnet test tests/TradeOps.UnitTests/TradeOps.UnitTests.csproj --filter FullyQualifiedName~IbkrOfflineOrderResponseParserTests
```

All fixtures are invented, with no broker session. Tests assert documented
success forms, confirmation prompts, chained replies, HTTP-200 error
classification, duplicate JSON keys, ambiguous multiple responses,
malformed payloads, safe handling of warnings, and lifecycle state gates.

Autonomous WSL2 runner will independently restore/build/test each Draft PR
HEAD and report JSON:

`avysotsky/TradeOps-Relay-Queue/autotest-results/tradeops/pr-<PR>/<SHA>.json`

**Limitations:** Contract is unverified against a real authenticated IBKR
session. Strict parser intentionally rejects unfamiliar shapes; these require
new validated fixtures, not silent acceptance. No durable idempotency
journal, broker reply confirmation transport, concurrent order coordinator,
production reconciliation, event sequencing or live transaction semantics
is implemented. No merge/NuGet/release without owner approval.
