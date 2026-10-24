# TO-V2-42 — Deribit-style VERTICAL DOM for RAW market depth

## Owner's accepted RAW feed

Owner accepted the working PR #44 SHA
9d9abe45412641c72ac534c3d3239f2862639f06:
- React/Deribit RAW WebSocket live orderbook and private positions visible.
- Owner reported exchange-to-local Δ around 251ms, and perceived DOM
  updates faster than the Deribit website. This is a single observed
  sample, not a p50/p99 latency benchmark.

Owner explicitly rejected the earlier two-column Bid/Ask layout:
institutional Deribit-style DOM should put **asks above, bids below**,
closest executable prices adjacent to a central spread/mid line.

## New front end structure

The RAW full-depth L2 engine in .NET remains unchanged and continues
to apply ALL snapshot/delta messages, validate change IDs, and publish
the latest validated top 10 levels.

The React view is now a single vertical price ladder:

          Volume     PRICE      Cumulative
          3          2355.5          10         Ask
          2          2355.0           7         Ask
          5          2354.5           5         BEST ASK
              MID + SPREAD (units & bps)
          1          2354.0           1         BEST BID
          4          2353.5           5         Bid
          8          2353.0          13         Bid

- ASK prices sort descending top to bottom, from farthest to nearest
  best ask. The best ask is directly above the spread row.
- BID prices sort descending top to bottom, from best bid at the
  spread row down to distant bids.
- Both sides use native Deribit PRICE and AMOUNT values without
  artificial currency/unit conversion. Options preserve 8 digits.
- Cumulative depth for each side is summed from its OWN best level
  toward greater distance from the spread. Colored bars are normalized
  per side to the 10 displayed levels. Bars show resting liquidity,
  not recent trades, ownership, depth in USD or execution priority.
- Mid = (bestAsk + bestBid)/2; spread = bestAsk - bestBid;
  bps = spread/mid*10000. This is an indicative midpoint, not a
  guarantee that a trade can execute at mid.
- Current RAW connection status / exchange timestamp age and validated
  change_id remain displayed. On disconnect/gap, the validated book
  becomes unavailable and no stale spread is shown as current.
- React remains requestAnimationFrame-paced for display only.
  This new ladder changes NO market feed transport or ingestion rate.
- No price-cell click-to-trade action added. Manual trading ticket
  remains separate, mainnet trade execution flags forcibly false.

Deribit's own price ladder describes ASK red rows at the top, BID green
rows at the bottom and current market levels near center:
https://insights.deribit.com/education/deribit-price-ladder/

## Verification

- Pure TS model apps/trade-terminal/src/marketDepth.ts is covered by
  Node24 executable tests for order, cumulative values, spread, bps,
  no mutation of snapshot, fractional option prices, invalid/null
  state and max 10 levels per side.
- npm test uses an explicit test path, compatible with Windows npm.cmd.
- The existing single-command PINNED launcher for this PR runs
  npm test THEN npm run build before exposing private read-only DPAPI
  credentials to the compiled .NET backend.
- Build, tests, browser rendering and live Deribit depth for THIS SHA
  are not owner-verified at the time of PR authoring; leave Draft.

## Owner acceptance

1. Stop old terminal using Ctrl+C.
2. Launch new SHA-pinned branch:
   TradeOps/to-v2-42-deribit-vertical-dom.
3. Confirm Node market depth tests PASS and React/.NET builds PASS.
4. Confirm ETH-PERPETUAL RAW stream LIVE. Ascending prices would be a
   bug in Ask half: visually scan top 10 ASK rows downward (descending);
   best ask is immediately ABOVE spread.
5. Best bid must be immediately BELOW spread; its subsequent BID rows
   descend farther from market.
6. Check midpoint/spread and cumulative depth, ask red / bid green,
   order state remains EXECUTION DISABLED and operator prompt absent.
7. Repeat with option instrument and a dated futures contract.

This is a visual DOM/layout enhancement only, not evidence of
institutional p99 feed latency or readiness for real order execution.
