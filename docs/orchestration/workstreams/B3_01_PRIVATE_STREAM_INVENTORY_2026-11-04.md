# B3-01 private-event capture: existing implementation inventory

Issue: [#73](https://github.com/avysotsky/TradeOps-v2/issues/73), still **OPEN**.
Source baseline: `c897cc2591639b02c6be85ee5216b1028d696d57` after the separately
qualified [PostgreSQL test optimization](B2_POSTGRES_REUSE_2026-11-04.md).
Date: 2026-11-04. This step inventories the existing code and runs its existing
offline contracts. It does not implement or qualify the B3 runtime.

## Existing path and gaps

| Boundary | Current source / behavior | B3 consequence |
|---|---|---|
| Application port | `IExchangeEventStream.RunAsync` takes order/execution callbacks; `ExchangeStreamUpdates` has no AccountKey, epoch or quantity unit | Cannot feed a scoped authoritative capture path unchanged |
| Existing adapter | `DeribitPrivateWebSocketStream` implements that port; DI selects it for `DeribitTestnet` | Extend this adapter deliberately; do not add another independently running private stream |
| Endpoint | Configuration accepts only official testnet WSS; existing tests explicitly reject production | Mainnet read-only is not qualified; do not remove this guard while callbacks still reach legacy storage |
| Instruments | Subscribes to `user.orders.future.any.raw` and `user.trades.future.any.100ms` | Futures-only subscriptions; options and other kinds are not covered by the running subscription list |
| Authentication | Sends public/auth with client credentials, reads access_token; no read-only scope request/response validation or expiry handling | Add explicit least-privilege scope verification and connection renewal without leaking auth frames |
| Handshake | `WaitForResultAsync` discards messages with another/missing RPC id | Subscription notifications arriving during setup can be lost; need one correlated receive path and bounded capture |
| Framing | ReceiveTextAsync appends fragments to an unbounded MemoryStream | Add a strict maximum message size and explicit malformed/non-text handling |
| Heartbeat | Sets ClientWebSocket KeepAliveInterval only | No implemented Deribit public/set_heartbeat + test_request/public/test lifecycle or stale-stream deadline |
| Reconnect | ExchangeEventWorker retries the legacy stream with bounded exponential delay | No durable stream epoch, account-switch barrier or RECOVERING/RECONCILING state; backoff alone is not recovery |
| Processing | Worker awaits legacy callbacks in the receive path | No separate bounded inbox writer; storage stalls also block receiving |
| Legacy correlation | ExchangeEventProcessor looks up bare ClientOrderId/ExchangeOrderId, checks symbol/side, writes legacy Orders/Fills | These checks do not establish full account scope; B3 must not route new capture through this path |
| B2 store | OmsPostgresJournal is store-only and has no runtime DI/caller binding to this stream | Existing durable-inbox tests do not prove live private-event capture |
| Inbox v1 | Stores basic kind/instrument/quantity/price/time and optional local aggregate ID | Lacks full remote order-report replay facts and capture lifecycle; preserve v1 bytes/hash semantics |

The parser currently accepts objects/arrays with the relevant channel prefixes. It
skips several malformed/missing-ID records, falls back from optional `contracts`
to `amount` without retaining an explicit native unit, derives filled contracts by
ratio and clamps values, and invents local UtcNow when venue time is absent. Those
behaviors must not silently define the new immutable replay/dedup contract.
The parser accepting an options-shaped channel is not options stream qualification.

## Official protocol checks

The current official production documentation and AsyncAPI specification were read
on this date; no user credentials, account endpoints or trading methods were used.

- Trade IDs are unique **per currency**, while trade_seq belongs to an instrument.
  Preserve the venue namespace/currency with full AccountKey; a bare trade_id is
  insufficient across currencies/accounts. Private trade_seq jumps alone are not
  evidence of a missed private event. The stable execution key must also be checked
  against self-trade/maker-taker semantics before implementation.
  [User trades](https://docs.deribit.com/subscriptions/user/usertradeskindcurrencyinterval).
- `amount` uses USD for inverse futures/perpetuals and base coin for options/linear
  futures; optional `contracts` can be absent in historical trades. Preserve native
  values/units and use trusted instrument metadata; never label every amount as
  contracts. The order schema also distinguishes amount, contracts and filled_amount.
  [Trades](https://docs.deribit.com/subscriptions/user/usertradeskindcurrencyinterval),
  [orders](https://docs.deribit.com/subscriptions/user/userorderskindcurrencyraw).
- `user.changes` is a consolidated orders/trades/positions stream, while the current
  parser handles separate orders/trades only. Selecting a wider channel alone does
  not implement its nested schema or qualify complete account coverage.
  [User changes](https://docs.deribit.com/subscriptions/user/userchangeskindcurrencyinterval).
- Deribit heartbeats require public/set_heartbeat (minimum 10 seconds); test_request
  requires public/test. Transport keepalive alone does not implement that protocol.
  [Connection management](https://docs.deribit.com/articles/connection-management-best-practices).
- Unrequested permission scopes inherit the key's permissions; read-only capture
  must request restricted scopes and verify what the server actually grants.
  [public/auth](https://docs.deribit.com/api-reference/authentication/public-auth).

The source schema's raw-order-kind enumeration and AsyncAPI's broader kind list
are not perfectly identical. Pin an explicit futures/options subscription set and
check the actual subscribe response; do not infer support from `any` or a permissive
parser. No live channel eligibility/coverage was tested in this inventory.

## Next bounded implementation step inside #73

Introduce the scoped capture application port and an explicitly versioned normalized
envelope, preserving old inbox v1 data. Separate immutable venue facts from capture
metadata (epoch, receipt time, connection identity). Reconnect must not change the
execution dedup key or create a collision merely because receipt metadata changed.
Include remote order/execution IDs, native quantity/unit, status/cumulative/leaves,
side, price/fee/currency, instrument and venue timestamp; represent unknown facts
explicitly rather than inventing quantities, timestamps or account mappings.

Persist through the scoped durable inbox before any read-model callback. Keep the
new capture path disconnected from legacy Orders/Fills until a verified additive
scoped mapping exists. Extend the existing adapter with one bounded receive path,
protocol heartbeat, cancellation/reconnect/epoch and stale-account-stream rejection.
After disconnect/gap/account switch remain RECOVERING/RECONCILING until B4 proves
snapshot/history/fills coherence. No sender/dispatcher or execution enablement.

Order reports need their own honest event identity/revision policy: order_id plus
millisecond timestamp is not a documented unique report revision. Do not manufacture
an execution ID from epoch or silently alter the v1 payload/hash to make a replay fit.
Capture-only acceptance and live private WS qualification must be reported separately.
FIX 4.4/QuickFIX/n remains mandatory later; this inventory does not qualify FIX.

## Actually executed

- Existing DeribitAdapterTests: **PASS 7/7** (including both theory rows per method).
- Existing DeribitWebSocketMessageParserTests: **PASS 3/3**.
- Deribit-specific ExchangeServiceRegistrationTests: **PASS 2/2**.
- Combined raw TRX: **12/12 PASS**, 0 failed/skipped; no network exchange connection.
- Resource Guard before/after: **PASS**. No runtime configuration or credentials changed.
- B3 account-switch/epoch/recovery/buffering/durable-capture tests: **NOT_RUN**;
  the necessary new behavior is not implemented yet.
- Live private WS, mainnet read-only qualification, venue-mutating testnet checks,
  legacy backfill/read models and FIX qualification: **NOT_RUN**.

Raw TRX/logs and the downloaded official schema/index are in ignored
`.tradeops/b3-01-inventory/`. No duplicate Issue/PR or parallel worker was created.
