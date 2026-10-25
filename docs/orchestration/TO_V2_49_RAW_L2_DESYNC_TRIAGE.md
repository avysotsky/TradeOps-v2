# TO-V2-49 — RAW L2 desync isolation (diagnostic Draft)

## Safety and scope

Deribit MAINNET remains READ-ONLY. The launcher forces both
`DeribitTerminal__ExecutionEnabled=false` and
`DeribitTerminal__MainnetExecutionEnabled=false`.
No order placements/cancels or exchange credentials enter logs, PR comments,
GitHub files or the diagnostics payload.

This slice **does not claim to have proven the cause** of the approximately
once-per-second RESYNCING symptom reported on PR #51. The exact prior
Windows process SHA and an exchange trace were not supplied.

## Code changes

- `DeribitRawBook` retains the exact RAW `prev_change_id == last change_id`
  validation, fail-closed behavior and crossed-book guard. On failure it now
  classifies L2_PREV_CHANGE_MISMATCH, L2_MALFORMED or L2_CROSSED and retains
  only safe exchange sequence IDs (expected, incoming previous and incoming).
- `DeribitRawFeedHub` emits sanitized categorized error counts in the
  status stream; it classifies WS_TIMEOUT, WS_CLOSED, WS_MALFORMED_FRAME,
  WS_AUTH_SUBSCRIPTION and WS_UNEXPECTED. RECONNECT_ATTEMPT is counted.
  Malformed *private* snapshots and notifications are isolated to the own
  order overlay, without invalidating a valid *public* L2 book.
- Browser displays diagnostics **only** in the collapsed
  `Диагностика рыночного потока` section. It separately counts local browser
  WebSocket closes (CLIENT_WS_DISCONNECT). The normal compact DOM is preserved.
- The RAW exchange pipeline still applies every received book update;
  `prev_change_id` checks are not disabled; no REST book polling introduced.

## Owner acceptance

1. Stop prior launcher with Ctrl+C. Use the exact SHA-pinned command for this
   branch supplied with this Draft PR. Do not run older unpinned builds.
2. Launcher verifies FETCH_HEAD equals ExpectedHead, builds API, runs isolated
   RAW L2 xUnit tests, runs npm tests/React build, then starts read-only
   localhost services. Any failure stops **before** the vault is opened.
3. Open the same instrument for 3–5 minutes; keep Depth=10, then switch to 25
   and 50. Record the error code and counter values, not the account data.
4. If RESYNCING occurs, open the collapsed diagnostics panel and copy **only**
   these non-private fields: Public L2, Private overlay, last failure code,
   expected change_id, incoming prev_change_id, incoming change_id,
   reconnect attempts, counters, CLIENT_WS_DISCONNECT. Do **not** send
   screenshots containing positions, orders, account balances or instrument
   names of personal positions.
5. Distinguish:
   - L2_PREV_CHANGE_MISMATCH: upstream gap / backlog / out-of-order;
   - L2_MALFORMED: unexpected RAW schema, row, numeric type;
   - L2_CROSSED: computed bid >= ask;
   - WS_TIMEOUT / WS_CLOSED / WS_MALFORMED_FRAME: transport/framing;
   - WS_AUTH_SUBSCRIPTION: exchange RPC/auth/subscription error;
   - WS_UNEXPECTED: an unclassified .NET failure (needs further local safe
     instrumentation);
   - CLIENT_WS_DISCONNECT only: browser/proxy disconnect, not necessarily L2.
6. Do **not** interpret UI status LIVE RAW alone as CI pass or latency proof.
   Next phase requires applying a root-cause-specific correction and measuring
   receive/parse/publish throughput, CPU, GC and tail latencies.

## Verification status

Offline source modifications and static checks only until new GitHub Actions
and/or Windows operator test reports exist. PR stays **Draft/unmerged**.
Full sustained mainnet sync is not verified.
