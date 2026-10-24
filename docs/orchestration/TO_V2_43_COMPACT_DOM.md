# TO-V2-43 — Compact Deribit-style vertical DOM

## Owner request

The previous Deribit-style vertical book from Draft PR #45 was
unnecessarily spacious. The operator requires exchange-like compact
depth with minimal horizontal and vertical padding, and columns in
this exact left-to-right order:

  PRICE | AMOUNT | CUMULATIVE AMOUNT

Asks remain ABOVE the midpoint/spread; bids remain BELOW. The closest
best Ask/Bid prices are adjacent to the spread.

## Frontend-only changes

- React: reorders columns to Price, Amount, Cumulative for both sides.
- DOM grid: fixed 105px / 88px / 102px columns with 4px gaps and 5px
  side padding, instead of three flexible 1fr columns (which stretched
  into excessive dead space). On narrow screens: 92/80/94px and 3px gap,
  with horizontal scroll if genuinely necessary.
- Level rows: 23px regular / 24px best-level, compared to 35/39px before.
- Spread divider: 30px versus 54px, smaller but still legible.
- Market feed grid: book panel capped around 355px on wide screens;
  other market data can use remaining screen width.
- Depth volume bands, row color and numeric tabular alignment retained.
  Removed animated width transitions to avoid unnecessary repaints.
- No change to 16px portfolio positions/orders tables.
- No changes to .NET order-book reconstruction, upstream RAW WebSocket,
  risk management, Deribit credentials, exchange APIs or trading routes.
- Existing one-command no-password Windows DPAPI mainnet read-only
  launcher points to this exact new branch and runs npm tests before
  React compilation. Both mainnet trade execution flags stay FALSE.

## Validation

Node tests: marketDepth.test.mjs (existing level sorting/cumulative
depth) and domCompact.test.mjs (new column order and CSS layout,
WebSocket-only feed / no click-to-order).
CI source is inherited; new PR owner run required. Builds, tests
and visual acceptance for this new SHA have NOT YET BEEN OBSERVED.
Use one pinned launcher; verify test PASS then React build PASS
and visually compare spacing with Deribit.

## Acceptance criteria

- Top to bottom: red Asks -> spread/mid -> green Bids.
- Per row left-to-right: price, amount at that price, cumulative
  resting volume from the market toward that level.
- Narrow fixed layout with compact 23/24px prices rows.
- Market data still uses event-driven RAW WebSocket and no 8s polling.
- No manual password prompts, no live trade execution.
