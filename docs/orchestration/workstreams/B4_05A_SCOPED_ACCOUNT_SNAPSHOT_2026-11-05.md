# B4-05a — scoped read-only positions and balances

2026-11-05. Implementation `04a2762343d77b3dd8036a410607ca72016a7c9d`.
An Application port and independent Deribit connector now provide bounded private
account observations with explicit native units, requested coverage and freshness.
This is offline/DB-only qualification; there is no live account-state claim.

## Delivered

- One explicit BTC/ETH currency and future/option kind per request, exact scope and
  correlation epoch, immutable trusted manifest from the capture binding. Inverse
  futures retain native USD size and independent base size; options retain base-coin
  size. Linear futures and other unsupported groups fail before network I/O.
- Selected-currency cash balance and equity remain distinct, including negative
  values and explicit cross-collateral/portfolio-margin flags. These currency-level
  balances include other product kinds; they are not allocated to the manifest.
- Restricted read-only session; summary → positions → summary identity bracket;
  pinned native account ID/type/currency and every accepted position's user ID
  checked. Shared fixed-origin RPC transport, no retries, 60-second deadline,
  512-KiB reply bound and explicit row capacity.
- Unknown instruments are not silently filtered; duplicate/malformed/foreign rows,
  partial failures and stale observations clear all private values. Empty arrays
  do not create zero positions. Requested instruments/currency/kind remain explicit;
  account-wide coverage is always false.
- Local request/receive windows, explicit 1–60-second maximum observation span,
  backward-clock detection and consumption-time `IsFreshAt`. No fabricated venue
  snapshot timestamp or common sequence; identity equality is not coherence.
- No durable encoding/schema change, persistence dependency, automatic live host,
  reducer/gate integration or execution switch. VenueCoherent, AccountReady and
  DurableProjectionPublished stay false. [Contract](../../architecture/DERIBIT_SCOPED_ACCOUNT_SNAPSHOT.md).

## Verification

Windows Release build with prescribed SDK/serial flags: exit 0, zero errors,
15 existing warnings. Actual `b4-account` TRX: **336 total/executed/passed**, zero
failed/skipped. Runner exit 0, environment/cleanup PASS. The suite includes the
previous 294 cases and 42 new scripted HTTP/request/freshness cases.

New cases cover native future/option units and long/short/zero values, negative cash
balance, scoped empty arrays, freshness at use, late/backward observations, wrong or
changed native identity/type/currency, foreign position user ID, unmapped/duplicate
rows, malformed/missing/overflow fields, grants/token/expiry, capacity, transport,
deadline, invalid request bindings and caller cancellation. Existing PG integrations
were rerun on owned disposable clones; all venue traffic remained scripted/loopback.

Evidence: `.tradeops/postgres-reuse/20261105T132524-b4-account-30717/`.
**314 normalized source hashes** matched; tested source is represented by the
implementation commit above. Test DLL SHA256:
`F386E9544A39B112717C8A032886DF954ED138396C599F7B6C95BCC406FA856B`.
Guard before/after PASS; peer container lifecycle snapshots unchanged (empty diff);
owned clone/role/session cleanup PASS. Reusable test server/template retained.
Owner launchSettings SHA256 remains:
`CBDDB50FE5C8379A82B3DD8B482696625C7F2F5DFB0058D2FD1D423653398183`.

No vault was decrypted, real venue session connected, mainnet store opened or
trade performed. The previous retained journal remains outside this iteration.
No private account values are present in committed fixtures or evidence reports.

## Next increment

B4-05b: add an explicit opt-in read-only qualification host/wrapper using the
existing saved key and encrypted native pin, validate the manifest through the
public instrument catalog, emit anonymous coverage/freshness/status diagnostics
only. Do not publish positions, balances, native IDs or credentials, and do not
run the old placement/cancel harness or terminal-order lookup again.

Then compose the account reader with Application assessment, keeping actual active
capture binding, local observation freshness and venue coherence distinct. Complete
relevant execution ledger, qualified venue snapshot/history cut, durable projection
and recovery gates remain; B4/READY, B5 physical fencing and #73 are still open.
This code increment does not authorize any new trading operation.
