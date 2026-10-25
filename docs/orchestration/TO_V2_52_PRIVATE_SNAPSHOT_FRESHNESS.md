# TO-v2-52 — Private REST snapshot freshness and epoch safety

## Reason for this change

After TO-v2-51 established that the Deribit RAW exchange-to-local delta was
dominated by Windows clock skew (owner measured corrected delta **29 ms**,
HTTPS get_time RTT **56 ms**), the next risk in the terminal is private state
freshness. The React terminal polls BTC/ETH account, positions and open
orders over authenticated private REST every 8 seconds, independently of the
public RAW book feed. Previously, a rejected request preserved old rows while
the summary continued to display old open-order/position counts as current.
The old implementation also allowed overlapping polls to finish out of order.

## Scope: read-only presentation safety

- Each private resource has a distinct state: `loading`, `ready`, or
  `unavailable`. A valid empty array is **zero confirmed orders/positions**;
  network/API rejection, malformed shape, or unconfigured keys are **unknown**,
  never represented as zero.
- The BTC and ETH account cards are accepted together or hidden together.
  Positions and orders may be available independently. A failure hides only
  the affected resource and displays an explicit warning.
- On a successful read, the heading shows the local UTC receipt timestamp.
  While a subsequent refresh runs, the last *timestamped* verified snapshot
  stays visible to avoid blinking large tables. If the new request fails,
  its old snapshot is revoked. None of these snapshots is a continuous
  account event stream; the public book feed is a separate WebSocket.
- A per-effect epoch gate forbids overlapping polls, and rejects any result
  from an aborted/re-keyed/unmounted polling session. All four requests
  share one AbortController and a 15-second timeout.
- Cancel buttons also remain disabled during an in-flight private refresh,
  and whenever open orders are not in state `ready` (they were already
  disabled by the mandatory mainnet execution flags).
- No modification to Deribit book reconstruction, subscriptions,
  `exchangeAgeMs`, `public/get_time`, controller order endpoints, preview
  IDs, or order execution logic.

## Strict safety boundaries

Mainnet launcher still forces both:
`DeribitTerminal__ExecutionEnabled = 'false'`
and
`DeribitTerminal__MainnetExecutionEnabled = 'false'`.

DPAPI credentials stay on the standalone .NET backend. Only normal
authorized private REST reads are used. No order mutations, new permissions,
DB migrations, external telemetry, or order IDs are sent elsewhere.

## Regression coverage and owner acceptance

`apps/trade-terminal/tests/privateSnapshotSafety.test.mjs` covers:
- ready-but-empty vs rejected, malformed and poisoned snapshots;
- all-or-nothing BTC/ETH account pair;
- in-flight epoch lock, invalidation, and late response rejection;
- React fail-closed presentation and both disabled execution switches.

The existing npm, TypeScript/Vite and .NET RAW tests remain in the
SHA-pinned Windows launcher before the DPAPI vault is opened.

Acceptance in MAINNET READ-ONLY:
1. Launch pinned TO-v2-52 SHA; verify both trading flags false.
2. Confirm the RAW book and clock diagnostics work unchanged.
3. Confirm positions, orders and account cards show UTC snapshot times.
4. A genuine empty open-order list must show `0 orders` only after a
   successful read. When private reads fail, the UI instead shows
   `Ордера недоступны` / `Позиции недоступны`, dashes in summary
   counts and no old private rows. After recovery the next poll restores
   verified data.
5. Verify repeated refreshes or reconnect/session reset never revert the
   browser to an older private snapshot. No real orders should be placed.

**Status:** implementation submitted as Draft PR; local owner Windows
runtime and CI checks must not be claimed as passed before they run.
