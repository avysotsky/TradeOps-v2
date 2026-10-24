# TO-V2-44 — Ultra-dense Deribit-style DOM and uniform price precision

## Owner request

After approving the compact vertical DOM concept, the operator requires:
1. **Even tighter vertical spacing** without sacrificing the legibility
   of all numeric values.
2. **The same number of decimal digits for EVERY price** displayed in
   a single selected instrument's ladder.

## Changes (React/CSS only)

### Density
- Normal price level: **18px minimum** (was 23px).
- Best Ask/Bid: **19px minimum** (was 24px).
- Column header: **20px minimum** (was 25px).
- Mid / spread: **24px minimum** (was 30px).
- Normal level has 1px vertical padding, a 15px line-height and 12px
  tabular fixed-width font. Depth remains accessible without shrinking
  the prices below 12px.
- The left-to-right columns remain PRICE | AMOUNT | CUMULATIVE;
  their spacing stays at only 4px. Red asks on top, green bids below.

### Fixed price precision per displayed snapshot
- Shared pure formatter `formatDomPrice(value, decimals)` uses BOTH
  minimum and maximum fraction digits, so trailing zeros are preserved.
- Shared precision `domPriceDecimals(ladder, tickSize, kind)` is
  determined once from Deribit instrument `tickSize` (when metadata is
  available), actual bid/ask levels, midpoint and absolute spread.
- When instrument metadata is unavailable (e.g. a held contract not
  among first 100 filtered instruments), prices themselves determine
  the required fractional digits, with minimum 1 for futures and
  minimum 4 for options, up to 8.
- Selected kind is inferred from the *actual selected contract*,
  not from a future/option search filter temporarily being changed.
- All ASK levels, all BID levels, MID and the absolute SPREAD use the
  **same** `priceDecimals` computed for their snapshot.
  Bps remains separately displayed at 2 digits, because it is a
  ratio, not an instrument price.
- Precision selection/formatting affects only React presentation;
  original double-valued exchange prices in the L2 book and execution
  engine are not rounded or modified.

For example, if tickSize is 0.5 and the midpoint needs 2 decimals:
  ASK 2355.0 -> 2,355.00
  ASK 2354.5 -> 2,354.50
  MID 2354.25 -> 2,354.25
  BID 2354.0 -> 2,354.00
  BID 2353.5 -> 2,353.50

For options 0.0016 / 0.0012 / 0.0010 / 0.0008:
  show exactly four digits for each price unless extra precision
  is required, including the midpoint/spread.

## Hard boundaries

- No change to .NET Deribit RAW WebSocket ingestion, per-message L2
  sequence validation, resynchronization or local WebSocket delivery.
- No market data REST polling introduced. Bid/Ask updates still arrive
  event-driven and React paints on animation frames.
- No orders, cancellations, broker writes, database changes or
  execution permissions. Both mainnet execution flags remain FALSE.
- Reuses existing read-only Windows CurrentUser DPAPI credentials and
  automatic ephemeral operator key; no user password prompts.
- Adds Node regression tests covering all price formatting sites,
  native future ticks, sub-cent option quotes, row sizes, 18/19px
  price levels and 24px spread. The one-command launcher runs
  `npm test` and `npm run build` before reading private credentials.

## Validation

Source changes are committed. New SHA remains DRAFT and **has not
been run on the owner's Windows workstation or independently verified
with a CI result**. Owner should stop previous terminal with Ctrl+C,
run exact-SHA launcher for this branch, check Node tests and builds,
and visually inspect dense ETH perpetual/orderbook and option pricing.
