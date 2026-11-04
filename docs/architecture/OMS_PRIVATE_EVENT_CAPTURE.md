# Scoped private-event capture (B3, store-only increment)

Issue: [#73](https://github.com/avysotsky/TradeOps-v2/issues/73).
This contract is the durable capture boundary, before WebSocket lifecycle integration
and B4 projections/reconciliation. It is not a live private stream, verified account
binding, coherent snapshot, fill projector or execution gateway.

## Version and account boundary

`IOmsPrivateEventCapture.CaptureAsync` accepts an `OmsPrivateEventEnvelope` with the
complete B1 AccountKey: tenant, Deribit venue, environment, account, optional
subaccount. Trusted server composition must supply that scope; this store does not
authenticate a remote account or infer its identity from an instrument/remote ID.
No browser endpoint or live DI registration is introduced in this increment.

Versioned venue facts use **event schema 2** in the existing `oms_inbox`. Namespace
v2 separates them from inbox v1. `OmsContractSchema.Version` and all old encoders,
hashes, namespaces and first-writer rows stay unchanged. The observation document
has its own **capture schema 1**, in new `oms_private_capture_observations`.
Neither schema is a legacy Orders/Fills backfill contract.

## Facts and identity

Facts retain the native instrument, trading currency namespace, own exchange order
ID, side, native amount with explicit unit/currency, venue UTC timestamp, optional
trade ID, native order status/cumulative/leaves, price kind/value, average price,
signed fee/currency, optional contracts and label. Missing data stays null. No
contract conversion, cumulative-fill clamping, leaves calculation, local timestamp
fallback or fabricated aggregate ID occurs here. The adapter must validate trusted
instrument metadata before constructing these facts; instrument eligibility and
native quantity interpretation are not qualified by accepting a DTO.

The immutable versioned JSON includes full account scope and facts, excluding all
receipt metadata. Decimal serialization uses invariant G29 without loss of decimal
precision, so 1 and 1.000 have identical v2 bytes/hash. This also stabilizes the
content-derived order-report key; v1's original decimal encoding is preserved.

| Kind | Namespace | Event ID | Meaning |
|---|---|---|---|
| Execution | `Deribit:execution-leg:v2:<currency>` | SHA-256 of the unambiguous JSON array `[trade_id, own_order_id]` | Currency and full AccountKey partition the venue trade; the participant's own order distinguishes legs. Changed side/instrument/quantity/fee under this key is a conflict, not a new fill |
| Order report | `Deribit:order-report:v2:<currency>` | SHA-256 of the entire normalized versioned fact document | Content-addressed observed report, not a promised venue revision. Distinct facts at the same timestamp are retained; no fill/state projection follows automatically |

Deribit documents trade_id uniqueness per currency and order_id as the subscriber's
own participating order. The leg key is a conservative capture policy, not a claim
of live self-trade/Block RFQ/combo qualification. No transaction-log rows are treated
as separate exchange executions. [Official user trade schema](https://docs.deribit.com/subscriptions/user/usertradeskindcurrencyinterval).

Optional fields are part of the declared fact document. A replay with different
known/unknown fields is conservatively quarantined for the same execution key;
this increment does not merge incomplete history observations into the first row.
That enrichment/reconciliation policy must be qualified separately. Quarantine
retains the incoming document and does not create another accepted execution.

## Receipt metadata and retries

An observation carries CaptureId, stream epoch, receipt UTC timestamp, source
(WebSocket/recovery query), and channel/query name. CaptureId identifies one
observation and stays constant across its exact retry. A newly received/reconnected
observation uses a new CaptureId. Epoch is metadata, never an execution identity.

- First accepted facts: `Inserted`; commit contains both inbox facts and observation.
- Same facts with a new receipt/epoch: `Duplicate`; another observation is committed,
  the first inbox JSON/hash stays untouched.
- Exact retry of an already accepted observation: `Duplicate`, never another
  `Inserted`. Exact retry of a quarantined observation remains `Collision`.
- Different facts for the same execution key: `Collision`; an integrity incident
  and the entire incoming facts/receipt are committed, without rewriting the winner.
- Reusing CaptureId for different input: `Collision`; the alternative observation
  and incident are committed, without inserting its potentially unrelated event.

Observation uniqueness is full account scope + CaptureId + normalized input hash.
Alternative inputs under a reused CaptureId can therefore be preserved as immutable
quarantine evidence. Observations deliberately have no accepted-event FK: a
quarantined alternative need not become an accepted inbox row. Their disposition
must be respected by future readers; observation rows are not fills.

## Transaction and privileges

One transaction arbitrates the full-scope CaptureId, compares/inserts the v2 inbox
row, appends any incident and persists its observation, then commits before returning.
A transaction-scoped advisory lock arbitrates observation retries; unique keys and
row locks arbitrate event races. These locks authorize no physical send. Losing a
commit acknowledgement can be retried with the identical observation; it cannot
justify dispatching an order or reapplying a fill. B4 must consume durable state,
not rely on an ephemeral callback after commit.

Existing inbox immutable guards remain active. New observations reject UPDATE,
DELETE and statement-level TRUNCATE. An ordinary application role needs schema
USAGE; SELECT/INSERT/UPDATE on inbox/observations (UPDATE permits FOR UPDATE locks);
SELECT/INSERT on integrity incidents and USAGE of its identity sequence. Mutation
triggers still reject evidence changes. No superuser/CREATEDB/CREATEROLE/replication
or TRUNCATE privilege is required. These are tested grants, not deployment changes.

The additive migration creates only the observation table/index/triggers. Its Down
refuses nonempty observations or v2 inbox evidence. Empty Down and re-upgrade can
preserve existing v1 rows byte-for-byte. All database validation uses the separately
marked test PG17, never the persistent development/production databases.

## Remaining B3 gates

The existing `DeribitPrivateWebSocketStream` still uses its legacy testnet path.
Next extend that adapter behind this application port, with strict read-only auth,
one bounded receive/capture path, size limits, heartbeat/test_request, cancellation,
renewal/reconnect, stream epoch and account-switch rejection. Invalid raw input must
produce a recovery/gap outcome rather than being silently discarded by a caller;
this store accepts only already normalized valid envelopes, capped at 32 KiB per
serialized document. It is not a raw packet archive or raw-frame parser.

After disconnect/restart/gap/account switch, RECOVERING/RECONCILING and coherent
snapshot/history/fills remain B3/B4 requirements. Legacy Order/Fill linking needs a
verified additive scoped mapping. No sender, dispatcher, risk bypass, physical
fencing qualification, FIX qualification or live readiness is provided by this store.
