# B4-05b — pinned read-only account qualification

2026-11-05. Implementation `a8e79832b70775fb6fef83fe7310747ca121a56e`.
The explicit read-only host/wrapper is implemented and tested. The single live
attempt returned **ACCOUNT_SNAPSHOT_INCOMPLETE / UnmappedInstrument**, not a
qualified account snapshot or successful recovery.

## Delivered

`--read-account-snapshot` branches before the order/store paths. Its purpose-bound
stdin accepts the existing saved key/native identity, validates active inverse
ETH-PERPETUAL metadata through the public catalog before sending credentials, and
uses the production scoped reader with a 30-second observation bound. Native
subaccounts are supported through the pinned identity, without subaccount selection
parameters. No database opener, capture session or trading call is used.

The Windows-DPAPI wrapper verifies the saved credential fingerprint against the
encrypted native pin, requires committed source, records source/assembly hashes
and passes credentials only over child stdin. Both ordinary execution switches
remain false. Output has an exact diagnostics allowlist; private result objects,
exception text and raw stdout/stderr are never persisted. Only validated primitive
fields and typed problems enter local evidence. Source/binaries are checked again
after the child exits. There is no automatic retry or manifest expansion.
[Contract](../../architecture/DERIBIT_SCOPED_ACCOUNT_SNAPSHOT.md).

## Offline verification

Windows Release build: exit 0, zero errors, 15 existing warnings, prescribed SDK
and serial flags. Actual `b4-account-host` TRX: **364 total/executed/passed**, zero
failed/skipped; runner exit 0, environment/cleanup PASS. This includes the previous
336 and 28 new scripted host cases: valid/empty/subaccount reads, metadata refusal
before auth, incomplete coverage, identity/grant/protocol/transport failures,
input bounds, cancellation, redaction and freshness revalidation.

The actual PowerShell output-validator function passed **22/22** independent
approved/malformed/malicious diagnostics cases without vault/process execution.
An executable entry check with empty input returned exit 1 and sanitized Input
refusal; it did not enter the order path.

Evidence: `.tradeops/postgres-reuse/20261105T134953-b4-account-host-22532/`.
**318 normalized source hashes** matched. Test DLL SHA256:
`C24365745033B37AAEADC7F334650F4DE4EF4BCD40A00FD8E8F84256E319E6A6`.
The four live host/connector/Application/Domain DLLs matched their copies loaded by
the test project. Host DLL SHA256:
`A4A3F5DD7043238F29924436E4300B9E5E4080E28F6ABDEE40339BC5C06FC689`.
Guard before/after PASS; peer container snapshots unchanged; owned test clone,
role and session cleanup PASS, reusable server/template retained. Scripted tests
used no real venue or vault. Owner launchSettings hash remains
`CBDDB50FE5C8379A82B3DD8B482696625C7F2F5DFB0058D2FD1D423653398183`.

## Single live attempt — incomplete coverage

`Deribit-AccountSnapshotLocal.ps1` ran once against the saved pinned mainnet binding,
after the implementation was committed. MetadataValidated=true. At
`2026-11-05T13:54:04.2786106Z`, the result was **ACCOUNT_SNAPSHOT_INCOMPLETE** with
**UnmappedInstrument**, process/wrapper exit 1. No automatic retry was made.

The ETH/future response contained an instrument outside the explicit one-instrument
ETH-PERPETUAL manifest. No name, size, balance or native account ID was published.
The result's accepted PositionCount=0 and BalanceObserved=false mean all private
observations were withheld; they do **not** mean the account has zero positions or
funds. QueriesCompleted, NativeBindingVerified and FreshAtAssessment are false for
this incomplete result. AccountWideCoverage, VenueCoherent, AccountReady,
DurableProjectionPublished and TradingReady remain false.

Live evidence:
`.tradeops/account-snapshot/20261105T135401-031b9e3397cc44c2a046b8ebe1c59820/summary.json`.
SourceCommit and all four assembly hashes match the tested implementation.
DatabaseUsed=false, DurableWrites=0 and MutatingCalls=0. No mainnet journal was
opened or audited in this increment; no placement/cancel harness or old terminal
order lookup was rerun. No position was modified and no durable projection published.

## Next increment

B4-05c must explicitly define and validate the intended supported ETH/future
manifest from trusted public metadata before another opt-in read. Do not silently
filter the unmapped row, infer units from its symbol, claim zero positions, or expand
to another account/currency/`any`. Unknown or unavailable historical metadata must
remain an explicit blocker. The current ETH-PERPETUAL-only host remains intentionally
incomplete for this response.

After qualifying that coverage, integrate the account port with Application
assessment. Venue snapshot/history coherence, complete relevant execution ledger,
durable projection/recovery gates and B5 physical fencing remain separate;
B4/READY and #73 stay open. No new trading operation is authorized by this increment.
