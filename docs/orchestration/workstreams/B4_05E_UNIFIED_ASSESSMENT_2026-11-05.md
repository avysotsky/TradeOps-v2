# Unified read-only B4 pipeline — implemented and qualified

2026-11-05. Implementation `42d2f50fa860985f1cfe4f74e74a567a9be76122`.
The separate readers now run together through the actual Application account
assessment, with the protected retained journal. The single live run completed as
**RECOVERY_ACCOUNT_EVIDENCE_BLOCKED**, with explicit unresolved recovery reasons.
This closes the unified diagnostic host/qualification deliverable, not B4/READY.

## Implementation and offline checks

Dedicated `--assess-account-recovery` and `Deribit-AccountRecoveryLocal.ps1` use the
existing saved DPAPI credential/native pin. Public ETH/future metadata is validated
before store/private access, then a marked SELECT-only reader opens the retained
qualification store. Production order-history/trade/known-order/account ports feed
`AssessAccountAsync`. Scope is explicit: orders/trades ETH-PERPETUAL, positions the
supported active inverse ETH/future manifest, balance ETH. There is no active capture
session, sender, migration/provisioning/write path, automatic retry or new trade.

The host accepts only a purpose-bound UTC recovery window, bounded JSON and valid
credentials/native mapping. Source lease disposal is guaranteed. Its output is a
dedicated versioned diagnostics DTO; the wrapper validates exact fields/types,
constant scopes, counts, typed problems, mandatory blockers, timestamps and exit.
No raw scoped identity, private facts, exception text or plaintext credentials enter
reports. The existing Ubuntu runner checks the owned server and reader role, resource
guard, peer lifecycles and journal count invariance. Source and five live DLL hashes
are bound to the tested inputs. [Contract](../../architecture/DERIBIT_UNIFIED_READONLY_ASSESSMENT.md).

Windows Release build: exit 0, zero errors, 15 existing warnings, prescribed SDK
and serial settings. Actual `b4-unified` TRX: **471 total/executed/passed**, zero
failed/skipped, runner exit 0, environment/cleanup PASS. Prior 445 plus 24 new
host/input/source/redaction cases and two real PG cases. Those cases exercise the
production connectors against scripted HTTP with an actual retained terminal order
and an actual integrity incident; host read counts remain unchanged and no expired
terminal lookup is sent. The actual Windows validator passed **25/25** cases without
vault or venue. Console mode + real validator with empty input returned sanitized
Input refusal and exit 1, without database access.

Offline evidence: `.tradeops/postgres-reuse/20261105T151636-b4-unified-1081/`.
**328 normalized source hashes** matched. Test DLL SHA256:
`EDAB3E3BAC941B2252C8DEA472601CE4F6167A82E51845F0C547822D1DD07A38`.
Guard/peer comparison and owned clone/role/session cleanup PASS, reusable server
retained. All five live DLLs matched the test-project copies. Host DLL SHA256:
`3BADE45507B8A7ED83DEDF840134E1D818CF8A1CB5242BC5D5B098265CC12A30`.

## Single live result

At `2026-11-05T15:23:03.2303999Z`, one invocation completed with host/wrapper exit 0:

- AssessmentCompleted=true, MetadataValidated=true, DatabaseUsed=true, SelectOnly=true.
- Public manifest 13, scoped account observations validated, accepted position rows 2,
  balance observed; AccountProblems empty. No private values/names/IDs were emitted.
- RetainedFacts=2, EnumeratedFacts=0, KnownOrders=1, AddressedOrders=0,
  RetentionExpiredOrders=1. Zero enumerated facts do not prove zero executions or
  full history. The stored terminal order was not looked up again.
- AccountWideCoverage, VenueCoherent, AccountReady, DurableProjectionPublished and
  TradingReady=false; DurableWrites=0, MutatingCalls=0. Exit 0 means the diagnostic
  ran successfully, not recovery completion.

Nine blockers remain:

1. HistoricalUnfilledCoverageGap.
2. OrderHistoryIncomplete.
3. CoherentCutUnavailable.
4. BufferedReplayUnavailable (this plain host has no active capture session).
5. ExecutionLedgerCoverageUnproven.
6. KnownOrderRetentionExpired.
7. KnownOrderUnavailable.
8. AccountSnapshotCoherenceUnproven.
9. AccountWideCoverageUnproven.

The persistent journal audit before/after was identical: Commands/Attempts **2/2**,
Inbox/Observations/Incidents **2/2/0**. Database diff and peer container lifecycle diff
are empty; live guard before/after PASS. No server/network/volume was started,
restarted or deleted. No placement/cancel harness was rerun and no position changed.

Live evidence:
`.tradeops/account-recovery/20261105T152249-400c9f6ddb0c4e00ae37ed0a947ce9fb/summary.json`;
environment/audit evidence:
`.tradeops/mainnet-recovery/20261105T152251-assess-account-recovery-8450/`.
SourceCommit and five assembly hashes match the tested implementation.
Owner launchSettings hash remains
`CBDDB50FE5C8379A82B3DD8B482696625C7F2F5DFB0058D2FD1D423653398183`.

## Remaining delivery results

The [owner-facing B4 → QuickFIX/n plan](../B4_TO_QUICKFIX_PLAN_2026-11-05.md) now
records this completed checkpoint. Next are complete supported coverage/coherence,
persisted recovered state and end-to-end B4 readiness/failure qualification, then
mandatory risk/physical sender ownership and FIX qualification. Old unavailable
history/terminal lookups are not repeatedly polled as a substitute for proof.
No new trading operation is authorized; #73 and overall B4/B5 readiness stay open.
