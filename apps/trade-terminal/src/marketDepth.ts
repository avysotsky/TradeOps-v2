/**
 * Deribit-style vertical DOM (read-only presentation model).
 * Exchange raw L2 book reconstruction remains in .NET; this transforms
 * ONLY its already-validated top 10 levels for React display.
 */
export type MarketLevel = { price: number; amount: number };
export type DepthRow = {
  side: 'ask' | 'bid';
  price: number;
  amount: number;
  cumulative: number;
  percent: number;
  best: boolean;
};
export type Spread = { bid: number; ask: number; mid: number; absolute: number; bps: number };
export type PriceLadder = { asks: DepthRow[]; bids: DepthRow[]; spread: Spread | null };

function levels(raw: MarketLevel[], side: 'ask' | 'bid'): DepthRow[] {
  const source = raw
    .filter(x => Number.isFinite(x.price) && x.price > 0 &&
      Number.isFinite(x.amount) && x.amount >= 0)
    .sort((a, b) => side === 'ask' ? a.price - b.price : b.price - a.price)
    .slice(0, 10);

  let total = 0;
  const accumulated = source.map((row, i) => {
    total += row.amount;
    return {
      side,
      price: row.price,
      amount: row.amount,
      cumulative: total,
      percent: 0,
      best: i === 0
    } satisfies DepthRow;
  });
  const max = total;
  for (const row of accumulated) {
    row.percent = max > 0 ? 100 * row.cumulative / max : 0;
  }
  // Exchange gives best ask first. To place best ask directly above
  // the mid/spread, the ASK HALF of the vertical DOM must be reversed.
  return side === 'ask' ? accumulated.reverse() : accumulated;
}

export function toPriceLadder(
  book: { bids: MarketLevel[]; asks: MarketLevel[] } | null
): PriceLadder {
  if (!book) return { asks: [], bids: [], spread: null };
  const asks = levels(book.asks, 'ask');
  const bids = levels(book.bids, 'bid');
  const bestAsk = asks.length ? asks[asks.length - 1].price : null;
  const bestBid = bids.length ? bids[0].price : null;
  if (bestAsk === null || bestBid === null || bestAsk <= bestBid)
    return { asks, bids, spread: null };
  const mid = (bestAsk + bestBid) / 2;
  return {
    asks,
    bids,
    spread: {
      bid: bestBid,
      ask: bestAsk,
      mid,
      absolute: bestAsk - bestBid,
      bps: mid > 0 ? (bestAsk - bestBid) / mid * 10000 : 0
    }
  };
}

/**
 * One immutable decimal scale for EVERY price printed in the same snapshot:
 * all ask/bid levels, midpoint and absolute spread. Deribit instrument tick
 * size (when present) sets the lower bound. Observed market prices may demand
 * extra digits, especially midpoint at a half-tick.
 *
 * The original exchange values remain Numbers in the data model. Formatting
 * affects the UI only; no rounding is used in trading or L2 reconstruction.
 */
function observedDecimals(value: number): number {
  if (!Number.isFinite(value)) return 0;
  // toFixed removes binary floating-point tails introduced by midpoint math;
  // 8 places is the terminal's present supported DOM precision.
  const fixed = Math.abs(value).toFixed(8);
  const fractional = fixed.split('.')[1] ?? '';
  return fractional.replace(/0+$/, '').length;
}

export function domPriceDecimals(
  ladder: PriceLadder,
  tickSize: number | null | undefined,
  kind: 'future' | 'option'
): number {
  const numbers = [
    // Reserve precision for a half-tick midpoint even when the CURRENT
    // bid/ask midpoint happens to sit on a whole tick. This prevents
    // visual decimal-width jumps as market spread changes over time.
    ...(tickSize && tickSize > 0 && Number.isFinite(tickSize)
      ? [tickSize, tickSize / 2] : []),
    ...ladder.asks.map(level => level.price),
    ...ladder.bids.map(level => level.price),
    ...(ladder.spread ? [ladder.spread.mid, ladder.spread.absolute] : [])
  ];
  // An absent instrument descriptor must not cause per-row variation.
  // Futures carry at least one fractional digit; options at least four.
  const base = kind === 'option' ? 4 : 1;
  return Math.min(8, Math.max(base, ...numbers.map(observedDecimals)));
}

export function formatDomPrice(value: number, decimals: number): string {
  if (!Number.isFinite(value)) return '—';
  const places = Math.max(0, Math.min(8, Math.floor(decimals)));
  return new Intl.NumberFormat('en-US', {
    minimumFractionDigits: places,
    maximumFractionDigits: places
  }).format(value);
}
