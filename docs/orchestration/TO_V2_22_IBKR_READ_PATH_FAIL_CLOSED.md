# TO-V2-22 — IBKR read-path fail-closed reliability

**Status:** Draft, offline-only, unmerged. Stacked on owner-qualified TO-V2-21
`9006db152885ddad33ecbc855aad191a8f1bf865`. GitHub PR and independent
exact-SHA relay report are the authority for qualification.

## Why this exists

Official IBKR Web API order-status documentation limits its endpoint to active
and cached orders. It explicitly warns that HTTP **503** can occur when a
linked-account session has not switched to the affiliated account, or when a
previously filled/cancelled order has no cached entry in the active session:
https://www.interactivebrokers.com/docs/web-api/v1/endpoints/order-monitoring/order-status

The previous `IbkrPaperExchangeClient.GetOrderAsync()` converted **404 and
503** to `null`. In the legacy `OrderReconciliationService`, `null` is
interpreted as an exchange lookup miss and may change a local order to
`Unknown`. No missing response can authorize order resend, placement, or
cancellation.

## Exact bounded changes

- Propagate HTTP 404/503 and all other non-2xx statuses as
  `HttpRequestException` with `StatusCode`. Never silently transform
  session/coverage failures into absent orders.
- Reject malformed `/iserver/account/orders` response envelopes instead of
  silently producing an empty order list. A **valid empty live-order list**
  remains valid for open-order display, but it is **not complete history**:
  a client-order-ID lookup that misses this list fails closed.
- Reject missing/mismatching account and broker order ID in individual
  order-status responses. Blank lookup IDs fail before any network request.
- Mark `IsConnected=false` when session preflight fails, even when the
  transport returned HTTP 200 with a non-Paper or invalid envelope.
- Synthetic HttpMessageHandler tests cover 404, 503, 401, 403, 429, 500;
  failed session revalidation, identity mismatch, invalid snapshots,
  and no inferred broker absence. Existing read-only Paper capability
  and mutation-denial checks stay intact.

## Important behavioral boundary

The legacy `IExchangeClient` API remains unchanged. This is *fail-closed
error propagation*, **not** a production cutover to the pure unified
`BrokerReconciliationDecisionEngine`. A failed IBKR lookup now aborts and
marks the existing reconciliation run as failed instead of silently
marking an order `Unknown` because of an ambiguous HTTP status.

A missing client order from a session-scoped *live* list is
`Unresolved` in the design sense, not confirmed absent. No automatic
retry/resend is implemented. The caller still needs authenticated
account-scoped evidence, durable sequence/digest and complete fills
before any trading-runtime integration can proceed.

**Out of scope:** automatic gateway login/reauthentication, keep-alive
scheduler, real session reconnect, WebSocket transport, actual market
data, CPGW/IBKR network calls, order place/modify/cancel, production
database migrations, new DI registrations, API/Worker behavior changes,
and altering owner-qualified workstreams.

## Qualification and handoff

Gate A requires a fresh independent WSL2 exact-head restore/build/
xUnit run (0 failed, 0 skipped), cleanup 6/6. Report path:
`avysotsky/TradeOps-Relay-Queue/autotest-results/tradeops/pr-<PR>/<SHA>.json`.

New branch commits must not change TO-V2-21's accepted SHA.
No new PostgreSQL acceptance is demanded for this code-only local
read-path scope unless further runtime/schema work is added.
**Paper-account validation still not performed**; `broker_requests=0`
for offline tests. Keep PR Draft and do not merge/publish.
