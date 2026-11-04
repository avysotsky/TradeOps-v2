# B3-02 — finite testnet order/capture acceptance

Baseline `main@29725ca7aaa723ea67997e269dda4ab136fac75f`.
Branch `TradeOps/b3-02-testnet-order-capture`, continuation of
[#73](https://github.com/avysotsky/TradeOps-v2/issues/73).

The next useful exchange check is a controlled one-order testnet cycle rather
than more quiet mainnet connections. [The exact scenario, guards and local launch](../../architecture/DERIBIT_TESTNET_ORDER_QUALIFICATION.md)
are implemented, with no changes to production execution switches or host defaults.

## Implemented

- Separate fixed-host testnet executable and hidden-input Windows DPAPI launcher.
  Mainnet vaults are never read, copied or elevated. Credentials travel over child
  stdin, not process arguments, logs, Git or chat. No API secrets were enrolled.
- Diagnostic authenticated native-account pin and independent WS identity check,
  idle dedicated BTC account, current inverse BTC-PERPETUAL metadata/book and a
  closed test risk policy: minimum native amount, at most 10 USD, passive limit/GTC,
  post-only with rejection. No arbitrary URL or database target in input.
- Intent/command/outbox and dispatch-start commit before placement via the existing
  OMS journal. One placement only. Targeted label/BTC cancellation gets its own
  command/attempt, including after a lost placement ACK. No blind resend, cancel-all,
  edit, sell or automatic flatten. This acceptance policy is not the full B5 engine.
- Real existing coordinator/capture store under a restricted role in an owned
  clone. PASS requires committed open and cancelled facts for the generated label
  and pinned native order, zero filled quantity, explicit terminal exchange state,
  no capture problems/incidents and completed shutdown. A cancellation ACK alone
  is insufficient. A lost placement ACK cannot qualify the whole scenario.
- Non-secret flushed recovery marker before dispatch. A previous marker blocks a
  new order even after PASS; uncertainty requires reconciliation before a new run.
  A local lock/DB lease is not production physical fencing. Cleanup failure reports
  unknown send counts/state rather than fabricated zeroes. Test clones/roles are
  removed; the exchange recovery marker stays.

## Verification

- Windows Release build PASS, 0 errors, 15 pre-existing analyzer warnings; no SDK,
  dependencies, global settings or database schema changes.
- Initial 129-case suite PASS; final expanded **132/132 PASS**, 0 failed/skipped.
  Previous 117 cases plus 15 cases covering input/mainnet refusal, expanded grants,
  stale book/excess size, exact bounded terms, wrong native account, normal cycle,
  lost placement ACK, lost cancellation ACK, unresolved cancellation and an
  unexpected partial fill. Exchange replies are scripted; journal/capture DB is real.
- Each scripted send/cancel case asserts exactly one placement and one targeted
  cancellation, actual committed events and refusal to start a second attempt.
  An unresolved exchange state or unexpected fill never reports PASS.
- CLI mainnet-input refusal PASS without DB/network use. PowerShell parser and
  Bash syntax checks PASS. Local credential Status: testnet credential absent,
  previous attempt absent, zero exchange-mutating calls from Status.
- Final evidence: `.tradeops/postgres-reuse/20261104T180238-b3-testnet-order-9007/`.
  Raw TRX outcomes/counters, source manifest, assembly hash, before/after guard,
  peers and database cleanup are verified before publication.
- Actual testnet authentication/order/cancellation: **NOT_RUN**. The explicit owner
  approval and a separate testnet credential remain missing. No mainnet order or
  real-money action was performed. Full unrelated suites, deployment and UI E2E
  were not run. B4/B5 readiness and production hierarchy remain unqualified.

## Next action

Obtain authorization for this exact first testnet scenario and enroll its separate
key via the hidden local prompt. Then execute one bounded cycle at the published
SHA and inspect exchange state, committed events and cleanup. Do not launch more
quiet mainnet sessions while waiting. After evidence from this cycle, implement
the recovery/history/fill cases needed by B4 and the full risk/sender gates in B5;
this first cancellation scenario cannot prove partial-fill or outage recovery.
