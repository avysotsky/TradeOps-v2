# B4-05a — scoped account observations

`IDeribitAccountSnapshotReader` lives in Application; its Deribit implementation
depends on Application/Domain and HTTP only. This increment introduces no database,
durable schema, journal encoding, reducer, automatic host registration or execution
switch. Existing recovery assessments retain all their blockers.

## Scope and units

One request names an exact `AccountKey`, nonempty correlation epoch, one BTC/ETH
currency and one future/option kind. The trusted capture plan supplies the explicit
instrument manifest and native account identity. A caller constructing that plan
must validate its instrument metadata through `DeribitInstrumentCatalog`; a bare
symbol or UI selection is not trusted metadata. The epoch is correlation, not proof
of a currently active capture generation.

Only inverse futures with USD order metadata and BTC/ETH options with matching
base-coin metadata are supported here. Linear futures, combos, spot, other currency
groups and an empty matching manifest are rejected before network I/O. The API
request uses explicit currency/kind filters and never `any` or `subaccount_id`.

Position `size` is preserved without conversion: USD for the supported inverse
futures, base coin for options. Futures also retain the independent `size_currency`
base-coin value, not a derived contract count. Direction and signed values must
agree; unknown direction, malformed/overflowing numbers and contradictory values
fail closed. The endpoint and fields are documented in
[Deribit get_positions](https://docs.deribit.com/api-reference/account-management/private-get_positions);
[Deribit's position description](https://support.deribit.com/hc/en-us/articles/25944587269021-Transaction-log)
describes long positions as positive and short positions as negative.

Balance contains only cash balance and equity denominated in the selected currency,
plus explicit cross-collateral and portfolio-margin flags. Negative cash/equity
values are retained. These two amounts are distinct, are not summed across currencies
and are not available margin or permission to trade. Their meanings follow
[Deribit get_account_summary](https://docs.deribit.com/api-reference/account-management/private-get_account_summary).
Balance/equity describe the selected account currency across product kinds; they
are not allocated to the selected positions manifest. Margin, PnL, leverage and
derived buying power are deliberately outside this contract.

The full returned positions array must fit the bounded request and known manifest.
An unmapped row causes `UnmappedInstrument`, rather than silent filtering; duplicate
rows, kind mismatch or a foreign native `user_id` also refuse the result. A missing
manifest instrument is not materialized as a zero position. The result records its
requested currency/kind and instruments, with `AccountWideCoverage=false` always.

## Reads, identity and freshness

There are at most four sequential JSON-RPC calls: restricted short-lived read-only
authentication, extended account summary, positions, and final extended account
summary for identity verification. Both summaries must match the pinned native
account ID/type and selected currency; every accepted position must match that
native ID. The first summary supplies the balance observation; the last does not
silently replace it. Identity equality does not demonstrate unchanged account state.

The existing fixed-origin allowlisted RPC transport bounds reply bytes, total bytes
and JSON depth, correlates response IDs and does not retry. Callers must supply a
redirect/cookie/default-credentials-free HTTP handler; default authorization/cookie
headers are rejected. Total deadline is 60 seconds; maximum accepted observation
span is explicitly 1–60 seconds. Positions are limited to 1–4096 rows (default 256).

Separate local request/receive windows are retained for balance and positions.
Neither endpoint supplies a qualified common venue snapshot timestamp/sequence in
this contract. The overall age starts before authentication, detects backward
clock observations and is rechecked by `IsFreshAt(now)` at consumption. Successful
I/O never confers permanent freshness. Timeouts, partial data, stale observations,
capacity limits and protocol/transport/authentication/identity errors clear **all**
account values and windows. Caller cancellation propagates; error messages never
include venue error payloads or private values.

`VenueCoherent`, `AccountReady` and `DurableProjectionPublished` remain false even
for fresh completed scoped queries. There is no reconciliation with buffered
private events, order history or the execution ledger. Consumers must not log or
serialize these private in-memory values into public evidence. A future opt-in live
host should publish only approved anonymous diagnostics.

## Qualification boundary

`b4-account` extends the B4 replay/gate acceptance set with scripted HTTP cases for
native units/signs, negative balance, scoped empty results, time boundaries,
coverage holes, changed identity, malformed fields, capacity, transport/auth failures,
invalid requests and cancellation. This is offline/DB-only qualification. It does
not use the saved vault, query mainnet, or prove a live recovery/READY transition.
