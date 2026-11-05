# B4-03c — scoped retained known-order addressed reads

2026-11-05. Implementation: `2dafd818b24d7b57b76b59019bb7ef49222021cf`.
The addressed read path is implemented and its negative live outcome qualified.
Fresh evidence for the old retained cancelled order remains unavailable; B4/READY
and historical-unfilled coverage are not completed.

## Delivered

- Application selection from the real scoped, integrity-checked snapshot. Latest
  reports for 1–32 known references; refuse foreign/problematic snapshots, unsupported
  dispositions, conflicting terms and non-monotonic retained progression.
- Separate application venue port and connector implementation, with no persistence
  dependency. READ-only short REST session, native account checks before/after,
  one addressed request per seed, bounds, deadline and no retries/fallback/trading.
- Native ID/instrument/unit/side/amount and timestamp/cumulative/terminal checks.
  Explicit Found/Missing/Unsupported outcomes, current facts cleared for incomplete
  sets or failures. Old facts are never rewritten or assigned a fabricated state.
- Qualification host composes the existing SELECT-only reader and connector in one
  process. IDs remain in memory; only anonymous diagnostics cross stdout or reach
  evidence files. No migrations, durable writes, sender registration or new roles.
- [Contract and primary API references](../../architecture/DERIBIT_KNOWN_ORDER_READS.md).

## Retention limitation

Deribit's current get_order_state documentation states that closed orders are only
available within the last 30 minutes and the method has no historical flag. Thus
an addressed lookup cannot restore general old fully-unfilled history excluded by
the historical API. A not-found reply is unavailable fresh evidence, not proof of
order absence, cancellation, zero fills or inconsistency of the retained cancel.
Repeated polling of this old order is not a recovery strategy.

## Verification

Windows Release build completed with exit 0, zero errors, 15 existing warnings,
using the prescribed SDK and bounded build options. Actual `b4-known-orders` TRX:
**173 total/executed/passed**, zero failed/skipped, runner exit 0. Environment/cleanup
PASS, source manifest **302 files** matched. The suite contains the prior 137 cases
plus 36 selection/addressed-read cases: scope/epoch/snapshot/terms checks, matching
found report, 10004/10033/-32601 unavailable outcomes, partial-set clearing, changed
identity, wider grant, mismatched/stale/future/reopened/quote responses, missing
cumulative amount, unknown/non-owner errors, HTTP failure, deadline and cancellation.
Only owned disposable PG clones were used for injection/destructive tests; collector
and addressed venue traffic in the suite is scripted.

Evidence: `.tradeops/postgres-reuse/20261105T101041-b4-known-orders-9522/`.
Test DLL SHA256: `DA8BC1D617644178614A68A2619EFFD632CD249CF6E471FF6C7650CEE5382FB1`.

Actual read-only pinned mainnet qualification, **one addressed lookup, no retry**:

- Existing persistent SELECT-only store reader accepted the retained snapshot:
  retained facts **2**, selected known orders **1**.
- Native identity checks before/after passed; addressed reply was venue **10004**.
  Result **KNOWN_ORDERS_UNRESOLVED**, QueriesCompleted=true, AllOrdersFound=false,
  Found/Missing/Unsupported **0/1/0**, CurrentFacts **0**, blocking query problems **0**.
- Qualification executable/runner exit **0** means the typed negative query and
  environment checks completed. It does **not** mean recovered order state or READY.
- CoherentRecovery=false, OrderHistoryComplete=false, TradingReady=false;
  durable writes **0**, mutating calls **0**, both execution switches false.
- Audit before/after: commands/attempts **2/2**, inbox/observations/incidents **2/2/0**,
  unchanged. Resource Guard before/after PASS, full container snapshots unchanged.
  Completed B3 one-order harness and attempt marker were not invoked or rewritten.
- Private summaries: `.tradeops/known-order-recovery/20261105T101431-dd3e2ae78bfe487abe89d5cb794439f3/`
  and `.tradeops/mainnet-recovery/20261105T101432-read-known-orders-19139/`.
- Qualification DLL SHA256: `D94FC16767F66D6D11997A4380BBB5221A1B39D64BD1AB81ACD95B3706F5BA91`.
  Owner launchSettings remains unchanged:
  `CBDDB50FE5C8379A82B3DD8B482696625C7F2F5DFB0058D2FD1D423653398183`.

This is live missing-evidence qualification. Found-report handling is scripted,
not a newly observed live cancellation or fill.

## Next increment

Implement application recovery orchestration/gating with explicit unavailable-evidence
and retention/coverage blockers, full scope/epoch binding and coherent outage/replay
requirements. Preserve retained terminal evidence and block unsupported completeness
claims; do not repeat a trade to refresh the old test case or retry unavailable old
state as though retention would improve. Positions/balances, durable projections/gates
and B5 physical sender fencing remain. Issue #73 stays open.
