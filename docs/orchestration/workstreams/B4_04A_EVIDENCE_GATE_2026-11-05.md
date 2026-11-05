# B4-04a — fail-closed application recovery evidence gate

2026-11-05. Implementation `d2be4cf3ea5fa31689234cdce9abf74083c1db10`.
This delivers read-side orchestration and explicit blockers. It does not complete
coherent recovery, durable projection publication, account READY or sender fencing.

## Delivered

- Application composes the existing scoped reader, bounded queries and known-order
  reader. No connector persistence dependency, migrations, new roles or durable writes.
- Trusted scope/epoch/units/window; snapshot binding, canonical count/collision and
  disposition validation before private venue I/O. Query source identity, pagination,
  bounds, provenance, timestamps and bindings checked independently.
- Known references already covered by compatible query reports avoid redundant
  addressed reads. Old terminal references outside get_order_state's retention window
  explicitly block; they are not polled again. Old open references remain eligible.
- Wrong/missing/unsupported addressed evidence, malformed indices, partial facts and
  substituted scope/epoch/snapshot remain blocking. Retained terminal facts survive.
- Found/quiet/exhausted queries cannot imply coherent cut, buffered replay or a complete
  lifetime execution ledger. The current gate returns no private facts/projection and
  never invokes the reducer with invented complete coverage. AccountReady and
  DurableProjectionPublished remain false.
- Explicit `--assess-recovery` qualification path using the existing pinned account
  and SELECT-only owned store, with anonymous blocker/count evidence only.
  [Contract](../../architecture/DERIBIT_RECOVERY_EVIDENCE_GATE.md).

## Verification

Windows Release build: exit 0, zero errors, 15 existing warnings, prescribed SDK and
bounded build options. Actual `b4-gate` TRX: **209 total/executed/passed**, zero
failed/skipped; runner exit 0, environment/cleanup PASS. The previous 173 cases are
included alongside 36 gate/orchestration cases covering binding substitution, stale
snapshots/results, invalid sources/windows/provenance/terms, contradictory reports,
missing evidence, request-index corruption, external orders, retention planning,
old open orders, quiet scopes, cancellation and prevention of false READY/publication.
No live venue or saved vault is used by this suite; PG injection stays in owned clones.

Private test evidence:
`.tradeops/postgres-reuse/20261105T104256-b4-gate-9387/`.
**305 normalized source hashes** matched. Test DLL SHA256:
`D30D51827C71143414A4EEDDEF8E57D7324D539029857B68698AC25F748AA34E`.

Actual pinned mainnet read-only assessment, executable/runner exit 0:

- **RECOVERY_EVIDENCE_BLOCKED**, CanRebuildOrders=false, AccountReady=false,
  DurableProjectionPublished=false, TradingReady=false.
- Retained facts **2**, enumerated current facts **0**, known orders **1**,
  addressed orders **0**, retention-expired terminal orders **1**.
- Exact blockers: HistoricalUnfilledCoverageGap, OrderHistoryIncomplete,
  CoherentCutUnavailable, BufferedReplayUnavailable, ExecutionLedgerCoverageUnproven,
  KnownOrderRetentionExpired, KnownOrderUnavailable.
- Five supported query sources passed their binding/window/source checks; no duplicate
  lookup of the old cancelled order was attempted. Quiet current queries do not
  prove full history, fill completeness or current cancellation.
- Durable writes **0**, mutating calls **0**. Before/after audit unchanged:
  commands/attempts **2/2**, inbox/observations/incidents **2/2/0**. Resource Guard
  before/after PASS, all container lifecycle snapshots unchanged. Both execution
  switches false; the consumed B3 harness/attempt marker was not invoked or rewritten.
- Private evidence:
  `.tradeops/recovery-assessment/20261105T104652-50516c3f22374ffca07920157a532179/`
  and `.tradeops/mainnet-recovery/20261105T104652-assess-recovery-26050/`.
- Qualification DLL SHA256:
  `FFAEC722F5920DD28CBC8B228A8A6C91E23B06F452C27CD73F8F13AB4302BF92`.
  Owner launchSettings SHA256 unchanged:
  `CBDDB50FE5C8379A82B3DD8B482696625C7F2F5DFB0058D2FD1D423653398183`.

This is live blocked-assessment qualification. No positive recovery, live fills,
coherent replay or trading readiness is claimed. Snapshot time checks are diagnostic
binding/staleness checks and do not establish PostgreSQL commit order.

## Next increment

B4-04b: qualified coherent cut and bounded buffered replay evidence tied to the active
capture scope/epoch, with explicit gaps/overflow/stale-epoch refusal. Do not replace
missing evidence with caller "complete" flags or extend query cutoff to receive time.
Then qualify complete relevant execution ledgers, positions/balances and durable
projection/recovery gates. Physical sender fencing remains B5. Issue #73 stays open;
no new trade or retry of the old unavailable terminal order is needed or authorized.
