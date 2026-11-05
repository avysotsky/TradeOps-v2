# B4 completeness — sufficient retained terminal evidence

2026-11-05. Implementation `15cd9e605e871aba833ae65a586032309cb966fb`.
The gate now derives historical terminal-order sufficiency from actual retained
reports and deduplicated execution facts. In the single live qualification this
resolved two order-specific blockers, leaving **seven global recovery blockers**.
It does not establish a complete ledger/account snapshot or grant READY.

## Rule and integration

Application `DeribitRetainedTerminalEvidence` validates the snapshot/current-query/
known-selection bindings and derives each outcome itself. An actual latest retained
terminal report, explicit cumulative quantity, consistent terms/report progression
and exact checked sum of unique native executions are required. No executions are
invented from cumulative quantity; empty venue results do not imply zero fills.
Quantity/price/fee collisions, missing details/fills, overfill, later venue events,
changed terminal state, wrong scope/epoch/cutoff and snapshot incidents refuse proof.
Cross-source duplicates count once, own order legs and currencies remain separate.

Expired references still increment RetentionExpiredOrders and are not looked up
again. Only sufficient retained terminal evidence increments the new validated
count and avoids adding KnownOrderRetentionExpired/KnownOrderUnavailable solely
for that reference. Unresolved references keep those blockers plus
RetainedTerminalEvidenceInsufficient. One valid order cannot clear another's gap.
Current available reports and still-open known orders retain their previous paths.
[Policy and authority boundary](../../architecture/DERIBIT_RETAINED_TERMINAL_EVIDENCE.md).

HistoricalUnfilledCoverageGap, OrderHistoryIncomplete, execution-ledger/account-wide
coverage and coherent-cut/replay requirements are unchanged. The rule is relative
to its supplied snapshot/query facts, does not absorb the later local replay payload
and cannot exclude unseen venue corrections. Existing reducer fresh-evidence rules
are not bypassed. Durable encoding/schema and normal execution switches are unchanged.
Unified diagnostics advance to v2, including count/blocker consistency checks;
older v1 reports remain historical files.

## Verification

Windows Release build: exit 0, zero errors, 15 existing warnings, prescribed SDK and
serial flags. Final `b4-terminal` TRX: **521 total/executed/passed**, no failures or
skips; runner exit 0, environment/cleanup PASS. The prior 471 cases were updated for
the policy, plus 50 new cases including three real PostgreSQL reader integrations
(zero fills, complete fills, missing fills). The real Windows validator passed
**30/30** cases. No fees/quantities are discarded to make a negative case pass.

The first full run had seven failures in a new gate fixture: query start preceded
the gate invocation on its fixed clock. Production time validation correctly
rejected those inputs. Fixture clocks were corrected without loosening production
checks; the complete final run passed. Failed-run evidence remains at
`.tradeops/postgres-reuse/20261105T161234-b4-terminal-32200/`; its cleanup passed.

Final offline evidence:
`.tradeops/postgres-reuse/20261105T162211-b4-terminal-12867/`.
**332 normalized source hashes** matched. Test DLL SHA256:
`4DDCECA565C42F75B0FC6672568BA6A10B9FCF51392319B492EB1AD253B03C31`.
Guard before/after PASS, peer container diff empty, owned test clone/role/session
cleanup PASS. No mainnet/vault was used in those tests. All five live DLLs matched
the test-project copies; host DLL SHA256:
`46AB64C660E9F3A8FD3FB735E1BF4F2EDE17900FAF0CC8D9FFCF3B5B6C9FC578`.

## One live read-only qualification

At `2026-11-05T16:27:51.4763581Z`, the existing pinned account pipeline completed
with **RECOVERY_ACCOUNT_EVIDENCE_BLOCKED**, exit 0:

- RetainedFacts=2, EnumeratedFacts=0, KnownOrders=1, AddressedOrders=0.
- RetentionExpiredOrders=1, **RetainedTerminalOrdersValidated=1**.
- Public manifest=13, accepted position rows=2, balance observed and scoped account
  observations validated; AccountProblems empty. Actual private values/IDs/names
  were not emitted. No old terminal lookup was sent and no trade was repeated.
- The two order-specific retention/unavailable blockers are absent. The seven
  remaining blockers are HistoricalUnfilledCoverageGap, OrderHistoryIncomplete,
  CoherentCutUnavailable, BufferedReplayUnavailable, ExecutionLedgerCoverageUnproven,
  AccountSnapshotCoherenceUnproven and AccountWideCoverageUnproven.
- AccountReady, VenueCoherent, AccountWideCoverage, DurableProjectionPublished and
  TradingReady=false; writes/mutating calls=0. Historical proof is not fresh venue
  authority and exit 0 is completion of the diagnostic, not recovery success.

Journal audit before/after: Commands/Attempts **2/2**, Inbox/Observations/Incidents
**2/2/0**, unchanged. Database/container lifecycle diffs empty, guard before/after
PASS. The persistent server and role were not modified.

Live report:
`.tradeops/account-recovery/20261105T162740-9f388222033a44829eca0466555bf744/summary.json`.
Environment/audit:
`.tradeops/mainnet-recovery/20261105T162741-assess-account-recovery-10508/`.
SourceCommit and five DLL hashes match the tested implementation. Owner launchSettings
hash remains `CBDDB50FE5C8379A82B3DD8B482696625C7F2F5DFB0058D2FD1D423653398183`.

## Next large result

The order-specific quantity sufficiency rule is now executable and verified. The
remaining completeness/coherence work must establish the relevant command/order/
execution universe and a common snapshot/history/capture basis; individual terminal
proofs cannot stand in for it. Then persisted recovered state and the full B4
failure/readiness lifecycle follow the [delivery plan](../B4_TO_QUICKFIX_PLAN_2026-11-05.md).
No new trade authorization or account READY follows; #73 remains open.
