# TO-V2-45 — Deribit DOM Last Trade / Mark / Index + exact tick price formatting

## Owner acceptance / requested change

Owner accepted compact RAW Deribit DOM density from PR #47 at SHA
1888b7c4856004636b0916adba55d1e6a6554d07, then requested:
- Remove inline DOM diagnostics and explanatory color legend.
- Move technical state to a separate area on the page.
- No mid-price or spread number in the order-book center.
- Replace with three values LEFT to RIGHT: Last Trade Price,
  Mark Price, Index Price, as on Deribit's market UI.
- Display exact **contract specification** price decimal precision,
  no deriving precision from current market prices or synthetic half tick.

## Source of live reference prices

The already-connected authenticated RAW Deribit market WS feeds
  ticker.{instrument}.raw
and
  trades.{instrument}.raw.

Ticker messages contain last_price, mark_price and index_price. The
React center row now uses their distinct market fields, keeping
the order Last / Mark / Index. Latest raw trade price is used only as
fallback if the ticker last_price is absent. Missing values show em
dash — instead of fabricated quotes. All reference values clear on
RAW stream disconnect / stale status, along with the book.

The static average midpoint and spread calculation remains in the
pure DOM model for validation but is NOT shown in the DOM; no
midpoint appears on the trading UI. References are real exchange
fields rather than inferred arithmetic.

## Source of instrument display tick precision

The selected symbol is fetched *separately* from authenticated local
  GET /api/deribit-terminal/market/instrument?instrument=...
which calls the public Deribit
  public/get_instrument

Contract specification reply contains tick_size and optional
tick_size_steps (above_price, tick_size). This works for held
instrument names outside the first 100 discoverable list results.
No REST market-data polling; one reference specification request on
symbol change (two bounded retries on failure). Strict fixed Deribit
HTTPS origin and named public endpoint, no private exchange data
access or trade side effects.

A single precision for the entire symbol is computed as the
maximum decimal places of the specified tick_size and all tick step
increments. Example:
- Future 0.5 => 1 decimal (2354.0, 2354.5)
- Future 1 => 0 decimals (2354, 2355)
- Option 0.0001 => 4 decimals (0.0010, 0.0011)
- Extra step 0.00005 => 5 decimals, so prices retain five places.
This scale also formats Last / Mark / Index; values are display
rounded to contract tick decimal precision as requested. It does
not round the underlying raw market values or alter trading prices.
There is no half-tick heuristic and no quote-dependent precision.
If spec cannot be fetched/validated, DOM prices show — and
diagnostics explain the missing specification, rather than
inventing a decimal scale.

Note: Deribit Index Price is an underlying index statistic, not
necessarily an executable price on the instrument tick grid;
it is merely formatted with the selected contract's decimal
places for consistency with the user-requested DOM display.

## Diagnostic placement

RAW state, message count, change_id, delta (exchange clock minus
local clock), tick size, rules count and precision live in a
native HTML details disclosure panel titled
  Диагностика рыночного потока
near the bottom of the terminal, outside market DOM. By default,
it is collapsed. The color legend and multi-line explanatory
paragraph below the DOM have been removed. LIVE RAW / RESYNC state
still appears in DOM header as important safety state.

## Unchanged boundaries

- No change to RAW L2/sequence engine, Deribit upstream WebSocket,
  trading order endpoints or risk logic.
- Existing one-command owner-local read-only launcher still requires
  no typed operator password, using ephemeral local key and Windows
  DPAPI exchange credentials.
- Both ExecutionEnabled=false and MainnetExecutionEnabled=false
  forced by launcher; NO live trades, cancels or account changes.
- Existing 18px DOM rows, Ask-top/Bid-bottom, Price | Amount |
  Cumulative columns, React requestAnimationFrame and 16px
  positions/orders grid remain unchanged.

## Gate

Draft/unmerged until owner runs exact pinned SHA and verifies:
1. npm tests and React/.NET builds pass before startup.
2. LIVE RAW ETH-PERPETUAL book, same dense vertical layout.
3. Center shows Last Trade Price | Mark Price | Index Price.
4. No MID value or spread amount in center.
5. All prices in DOM use the number of fractional digits specified
   by selected contract tick_size and optional tick_size_steps.
6. No legend/diagnostics text underneath the DOM; diagnostics expand
   only when operator clicks near page bottom.
7. Last/Mark/Index are non-stale and blank until data arrives;
   on connection drop invalid/stale quote state is cleared.
8. No order trading enabled.

Source references:
- https://docs.deribit.com/api-reference/market-data/public-get_instrument
- https://docs.deribit.com/subscriptions/market-data/tickerinstrument_nameinterval
- https://docs.deribit.com/changelogs/jsonrpc

No owner execution, CI pass or actual mainnet verification of this
NEW SHA has yet been observed. Do not claim production readiness.
