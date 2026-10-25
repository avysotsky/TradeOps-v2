# TO-V2-48 — Live exchange lag beside Depth and compact account tables

## User-requested layout

The owner currently sees a RAW L2 exchange-to-local indicator around
465 ms in the collapsible diagnostics and wants this indicator also visible
at the RIGHT edge of the same row as the selected DOM depth controls.

The row is now:

    Depth  [10 / 25 / 50]  RAW L2       Δ exchange → local: 465 ms

The number is bound to the already existing exchangeAgeMs value from
the live Deribit RAW book event stream. It is NOT a hardcoded sample.
On disconnected/resyncing/waiting for snapshot states the row displays
an em dash instead of the last stale sample. The delta depends on
synchronization of Windows and exchange clocks; it is not a verified
one-way network latency or network trading SLA. The existing detailed
diagnostics disclosure remains available.

## Dense positions and open-order grids

The user's accepted table type size stays 16px by default, adjustable
between 14–20px using existing A-/A+ buttons. We do NOT shrink
financial amounts to make tables compact. Only cell geometry changes:

- Both EXCHANGE STATE / Open Positions and ORDER MANAGEMENT / Active
  Orders share a scoped .data-panel override.
- Table body cells: padding 2px vertical / 8px horizontal, line
  height 1.25 (previously 18px vertical and 1.5 line height).
- Table headings: padding 4px vertical / 8px horizontal, line height
  1.2 (previously 16px vertical padding).
- State pills: padding 2px/6px; cancel buttons 2px/7px, both 13px
  labels and retained disabled state.
- Empty-data rows remain readable with 7px vertical padding.
- Mobile columns use 6px horizontal padding and preserve horizontal
  scrolling instead of compressing account values.

Default normal rows are approximately 24px high at 16px selected font
(20px text line-height + 4px vertical padding). Rows containing
interactive controls may be slightly taller where necessary.

## Boundaries

- Existing Deribit live RAW WebSocket, full depth L2, own-order overlay,
  10/25/50 depth selector, contract tick precision, and state
  reconciliation are unchanged.
- No API or broker changes; no PostgreSQL migration; no order/cancel
  requests; no live execution or secrets passed to browser.
- Mainnet ExecutionEnabled and MainnetExecutionEnabled remain false in
  the owner's one-command Windows DPAPI read-only pinned launcher.
- Existing no-password ephemeral operator session credentials remain
  as before.
- Branch has executable Node regression tests and React/.NET compile
  workflow. No owner workstation acceptance of this new SHA yet.
  Keep PR Draft and unmerged.

## Owner acceptance

1. Stop older launcher with Ctrl+C.
2. Run the new pinned launcher from this branch, verify Node tests and
   React/.NET builds before browser startup.
3. Inspect Depth selector toolbar: label Depth, controls 10/25/50,
   RAW L2, dynamic delta right aligned on same row.
4. Switch to 25/50 and confirm delta still updates, never blocks RAW,
   own order markers and Last/Mark/Index remain correct.
5. Inspect both Open Positions and Active Orders: rows appear dense,
   original text stays readable and A+ still enlarges it.
6. Confirm no mainnet execution is enabled or order event sent.
