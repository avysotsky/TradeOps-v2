# B4-03c — addressed reads of retained known orders

This slice follows the historical-unfilled gap in
[B4-03b](DERIBIT_RECOVERY_QUERIES.md). It does not infer cancellation from open-order
absence, erase retained evidence or turn an exhausted query into account READY.

## Composition and trust boundary

The application selects references through `DeribitKnownOrderSelection.FromSnapshot`.
Input must be an integrity-checked usable snapshot from the B4-02 reader, with exact
tenant/venue/environment/account/subaccount binding, valid snapshot ID/time and a
fresh nonempty epoch. Every envelope must retain that scope and a supported accepted/
duplicate disposition. Foreign/future facts, collisions/incidents, missing order
reports, conflicting terms, simultaneous contradictory reports, cumulative regression
and terminal reopening refuse selection. Latest reports are selected for the explicit
instrument; unique known-order references are bounded to 1–32.

`IDeribitKnownOrderReader` belongs to Application; `DeribitKnownOrderReader` lives in
the Deribit connector and has no PostgreSQL/EF/Infrastructure dependency. The separate
qualification host composes the SELECT-only owned store reader, selector, manifest
validation and venue port. IDs are selected from retained scoped facts, never from
UI input, labels or fingerprints. The host retains the original DPAPI native-pin
mapping; it does not discover a convenient account from database contents.

## Venue reads and refusal

The connector requests a unique 300-second READ session, checks the returned grant
and verifies native account ID/type/currency before and after the bounded batch.
Each seed gets exactly one `private/get_order_state` request with only `order_id`.
No retry, historical flag, label lookup, new order, cancellation or execution sender
exists on this path. The shared fixed-origin transport retains its bounded replies,
8 MiB aggregate limit, request spacing and redirect/cookie-free client requirement.
The batch has a 60-second deadline, up to 32 known references, and caller cancellation
propagates. Query inputs are copied before asynchronous processing.

Found reports must match native order ID, trusted instrument metadata/units, side
and amount, have non-regressing timestamp/filled amount, and cannot reopen a retained
terminal state. Missing cumulative amount, future timestamps and quote-order semantics
are refused. Recovery envelopes preserve scope/epoch/receive time and method provenance,
using the existing unchanged durable v2 normalization.

Only a bounded, correctly correlated JSON-RPC error for the addressed method can
produce a typed unavailable lookup: code 10004 is **Missing**, and 10033/-32601 is
**Unsupported**. Unknown codes, non-owner errors, wrong IDs, authentication/transport/
protocol failures remain blocking problems. HTTP 400 is parsed only for this method;
server/redirect/rate-limit failures are not interpreted as missing orders. Private
error messages/data are never emitted; only numeric codes and typed outcomes survive.

`QueriesCompleted` means every seed received a typed lookup and both identity checks
passed. It can be true while `AllOrdersFound=false`. A missing/unsupported order never
manufactures a state or invalidates the saved terminal fact. If any requested order
is unavailable or any blocking problem occurs, the current observation batch is
cleared, including otherwise found reports. Query diagnostics retain request indices,
not a public list of account/native order identifiers. `CoherentRecovery` stays false.

## Retention limitation

[Deribit's current order-state documentation](https://docs.deribit.com/api-reference/trading/private-get_order_state)
states that this method has no `historical` flag and returns closed orders only
within the recent 30-minute window. Thus addressed reads do **not** repair the general
historical-unfilled gap. An old retained cancelled order may legitimately be missing;
this is unavailable fresh evidence, not proof that it never existed or is cancelled
now. [The complete RPC error table](https://docs.deribit.com/articles/errors) documents
10004 as order_not_found, 10033 as not_implemented and -32601 as method_not_found.

## Explicit live qualification

After a bounded Windows build, `scripts/deribit/Deribit-KnownOrderRecoveryLocal.ps1`
requires committed source, decrypts the existing vault/native pin, verifies the pin
against the client-ID digest and launches the owned retained-store runner with
`--read-known-orders`. Secrets travel through stdin and only the already qualified
SELECT role is supplied in the process environment. The host derives references from
the actual scoped retained snapshot, validates ETH-PERPETUAL metadata, then requests
addressed state. Native IDs never leave that process in stdout, arguments or files.

The shell runner never starts/provisions/migrates a store on this path. Resource Guard,
full container snapshots and anonymous database counts are checked before/after.
Only sanitized statuses/counts/times/codes and source/assembly hashes are retained
under ignored `.tradeops/`. `KNOWN_ORDERS_UNRESOLVED` is a completed negative query
qualification with zero current facts; it is deliberately separate from
`KNOWN_ORDERS_FOUND` and from any coherent recovery/readiness transition.

No observations/projections/checkpoints are written by this slice. Positions/balances,
coherent stream replay, an explicit unavailable-evidence gate and physical sender
fencing remain subsequent work. No repeat trade is needed or authorized.
