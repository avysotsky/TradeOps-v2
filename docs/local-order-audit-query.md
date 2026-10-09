# Local order audit query

TradeOps exposes a bounded read-only query over persisted local orders:

`GET /api/orders/local`

This endpoint reads the local PostgreSQL order projection. It does not call the configured exchange adapter and does not submit, cancel, retry, reconcile, or flatten orders.

## Query parameters

- `symbol` — optional symbol filter; trimmed and normalized to uppercase; maximum 50 characters.
- `status` — optional current persisted `OrderStatus`: `Created`, `Submitted`, `Accepted`, `PartiallyFilled`, `Filled`, `Cancelled`, `Rejected`, or `Unknown`.
- `from` — optional inclusive lower bound on `Orders.CreatedAt`.
- `to` — optional exclusive upper bound on `Orders.CreatedAt`.
- `limit` — result bound; default 50; valid range 1..200.

When both `from` and `to` are supplied, `from` must be earlier than `to`.

Date/time inputs are normalized to UTC before querying PostgreSQL.

Results are ordered deterministically by:

1. `CreatedAt DESC`
2. `Id DESC`

## Persistence semantics

`status` is the current local persisted order status. The query does not answer when a status transition occurred.

For exact lifecycle history of an individual local order, use:

`GET /api/orders/local/{idOrClientOrderId}/history`

The local audit list is deliberately separate from:

`GET /api/orders`

which returns current open orders from the configured exchange adapter.

## Examples

Recent local orders:

`GET /api/orders/local`

Recent BTCUSDT local orders:

`GET /api/orders/local?symbol=BTCUSDT&limit=50`

Cancelled BTCUSDT local orders created in a UTC interval:

`GET /api/orders/local?symbol=BTCUSDT&status=Cancelled&from=2026-10-01T10:00:00Z&to=2026-10-01T11:00:00Z&limit=100`

## Performance/index policy

The initial bounded audit surface reuses existing persisted order indexes and adds no speculative index. In particular, no `CreatedAt` order index is added in this version without representative PostgreSQL plan evidence.
