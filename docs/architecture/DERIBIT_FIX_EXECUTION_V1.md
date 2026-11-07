# Deribit FIX execution v1 — B4/B5 integration

2026-11-07: this document preserves the original B6 `c1ac85b` boundary.
The additive [price-only mutation v2](DERIBIT_FIX_PRICE_AMEND_V2.md) extends it with
native G and Cancel-after-G. Its separate owner qualification procedure remains
LIVE NOT RUN; the historical 2351-case B6 acceptance does not certify that extension.

The opt-in FIX transport uses the existing B4 live recovery capability, durable B5
policy/admission/reservation, dispatch revalidation and the same provisioned OS
physical owner as WS. The connector depends on Application/Domain only. No FIX
configuration, session cursor, saved binding or JSON document grants send authority.

## Native contracts and recovery

`DeribitFixCommandBindingV1` is appended in the dispatch-start transaction, before
engine order entry. Its command hash, native session identity, connection epoch,
deterministic original client ID, verified currency/contract units and native cancel
parent are retained separately from JSON-RPC receipt v1. The protocol store also
commits the encoded outbound message and sender cursor before physical I/O.

`DeribitNativeCommandView` is an in-memory view of validated WS or FIX evidence.
It is not a new WS receipt or a private execution. Existing journal v1, private
event v2 and receipt v1 encodings remain unchanged. Recovery v1 adds nullable FIX
bindings/reports with `WhenWritingNull`, preserving canonical encoding of old
archives. No getter containing the new view is added to the archived DTO.

The B4 reader reads commands, attempts, outbox, WS receipts, native FIX bindings,
reports, semantic index and incidents in the same read-only repeatable-read
transaction. It checks canonical hashes, account/session/epoch/sequence, original
command links and native dedup integrity. Any FIX incident blocks qualification.
Placement mapping uses the deterministic original client ID (native tag 41), not
a label lookup. Server tag 11, order 37, change 527, ExecID 17 and trade-match 880
retain their distinct meanings.

FIX cumulative quantities constrain the WS/history model and are never added to
portfolio positions or fills. Complete native trade history, fees, coherence and
terminal proof remain mandatory. Partial or unknown outcomes retain pending risk.
Persisted native acknowledgement survives a lost callback ACK and restart; the old
capture capability cannot be reused. Missing acknowledgement remains unresolved,
without blind resend or fabricated receipt. Native cancel rejection is retained
and requires recovery; cancel ACK alone does not retire the original reservation.

Own addressed FIX cancellation uses the retained native placement and server
ClOrdID as OrigClOrdID, through the same admission/controls/permit boundary. It can
operate under kill only with the existing policy's current own-order proof. There
is no replace, cancel-all, automatic flatten, account adoption or HA takeover.

## Ingress, locks and physical owner

External FIX ingress invalidates the account revision under the same `replaySync`
barrier used by the physical permit, before acquiring the engine lock. Tickets do
not invent WS epochs or receipt ordinals. Fragments and queued sequence gaps hold
the barrier through framing, engine drain and native persistence. Fault/abandon
poisons the old baseline. A WS epoch restart cannot erase pending FIX ingress.
Recovery that overlaps pending ingress requires a new cut after drain.

Low-sequence PossDup messages still pass native journal validation when QuickFIX
would otherwise skip FromApp. Changed native payload cannot disappear as a replay.
The engine requests recovery again after drain, so an assessment that overlapped
pending bytes is retried from a fresh cut.

The gateway holds head, controls and evidence locks until the one-use permit has
invoked the actual physical socket operation. It then commits SQL before waiting
for the native response, allowing FromApp to retain evidence before advancing the
target cursor. Entering an async queue does not release SQL gates. Timeout/failure
revokes physical authority before rollback. Revoke → abort → drain → OS release
also applies to heartbeat, resend/gap-fill and all other engine writes. Business
replay remains suppressed, including across restart.

## Explicit host and identities

Select `Worker:Mode=DeribitOmsFixExecution`, `DeribitOms:Enabled=true` and
`DeribitFix:Enabled=true`. Supply `DeribitFix:SenderCompId`, the provisioned
`DeribitOms:LockRoot`, current B5 policy and four distinct database identities in
the same authoritative database:

- `TradeOpsCaptureDb`: existing private-capture role.
- `TradeOpsRecoveryDb`: existing recovery role, plus SELECT on the FIX binding,
  report, native-event and incident tables.
- `TradeOpsExecutionDb`: existing execution role, plus SELECT/INSERT on
  `oms_deribit_fix_command_bindings_v1` and SELECT on native reports/events/incidents.
  It has no native report/cursor write or session provisioning privilege.
- `TradeOpsFixDb`: separate protocol/native-capture role, checked by
  `OmsPostgresFixRuntimeGuardV1`. SELECT on protocol/native tables and evidence
  counter; INSERT on outbound messages, reports/events/incidents; UPDATE only
  `next_sender`, `next_target`, `last_logon_ms` on provisioned sessions. Only scope-key
  column INSERT on the evidence counter; no version write, session INSERT, command,
  risk or recovery write, DELETE or TRUNCATE.

Provision migrations/roles, node objects, policy and exact session identity out of
band. Runtime never migrates, grants, seeds or resets anything. For a session row,
`key_sha256` is `OmsRecoveryRunCodec.Encode(OmsFixSessionKeyV1).Sha256`; its full scope
and CompIDs must match the configured session. Keep secrets and native pin exports
outside version control. Account ID/type come from the trusted owner binding
(including the retained encrypted native pin when activating that account), never
from silently adopting a successful authentication response.

The transport independently authenticates a fixed-origin read-only JSON-RPC pin
with the same credentials before FIX logon and before preparing each command.
It checks native account ID/type/currency and rejects PM/cross collateral. Public
`get_instrument` supplies actual contract_size, units, active status, expiry and
amount step. Configured multipliers are not accepted as native metadata.

Both ordinary execution switches stay explicitly false. Default/legacy modes do
not register FIX. Exclusive opt-in composition registers no legacy sender or
strategy. Reader/tick supervision, TLS failure, disconnect, uncertainty or session
lifetime expiry stop the host; no reconnect/resend or automatic renewal. The
transport's maximum lifetime is 240 seconds; restarting requires fresh recovery.

## Qualified boundary

The supported FIX order profile is intentionally restricted to standard main-account
segregated inverse futures with separately verified **1 USD per contract**, native
USD limit/GTC, exact approved price/amount, reject-post-only and reduce-only flags,
and own addressed cancellation. BTC multiplier 10, options, linear products,
subaccounts, PM/cross, replace and ambiguous units are refused. No support is
inferred from instrument names or minimum trade amounts.

QuickFIX/n 1.14.1 and the compiled restricted Deribit FIX44 profile are used. The
profile was assembled from official production semantics; it is not vendor-issued
XML or the whole FIX44 specification. TLS uses fixed production/test endpoints and
platform chain/name/revocation validation. Hard sequence reset fails closed;
generation re-provisioning/archival requires a separate owner operation.

Offline/scripted engine, actual loopback TLS and owned PostgreSQL acceptance do
not establish live venue compatibility, F3 or trading readiness. This implementation
does not activate existing accounts, migrate permanent databases or send real orders.

References: [FIX production overview](https://docs.deribit.com/fix-api/production/overview.md),
[Execution reports](https://docs.deribit.com/fix-api/production/execution-reports.md),
[NewOrderSingle](https://docs.deribit.com/fix-api/production/new-order-single.md),
[Cancel](https://docs.deribit.com/fix-api/production/order-cancel-request.md).
