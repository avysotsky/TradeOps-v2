/**
 * Deribit-style vertical DOM (read-only presentation model).
 * The .NET backend owns either full RAW reconstruction or an exact native
 * 1/10/20-level grouped Deribit subscription. RAW remains capped to its
 * top 50 levels in React to keep DOM rendering bounded.
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

export type DomDepth = 1 | 10 | 20 | 'raw';

function levels(raw: MarketLevel[], side: 'ask' | 'bid', depth: DomDepth): DepthRow[] {
  const source = raw
    .filter(x => Number.isFinite(x.price) && x.price > 0 &&
      Number.isFinite(x.amount) && x.amount >= 0)
    .sort((a, b) => side === 'ask' ? a.price - b.price : b.price - a.price)
    .slice(0, depth === 'raw' ? 50 : depth);

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
  book: { bids: MarketLevel[]; asks: MarketLevel[] } | null,
  depth: DomDepth = 10
): PriceLadder {
  if (!book) return { asks: [], bids: [], spread: null };
  const asks = levels(book.asks, 'ask', depth);
  const bids = levels(book.bids, 'bid', depth);
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
 * Precision is defined ONLY by the selected Deribit contract specification
 * (public/get_instrument tick_size and optional tick_size_steps).
 *
 * Never infer decimals from mid-price, mark/index, spread or visible levels.
 * If exact metadata is absent, fail closed with an unavailable price display
 * instead of silently inventing another tick increment.
 */
export type InstrumentTickSpecification = {
  name: string;
  kind: 'future' | 'option';
  tickSize: number;
  tickSizeSteps: { abovePrice: number; tickSize: number }[];
};

function tickDecimals(tick: number): number | null {
  if (!Number.isFinite(tick) || tick <= 0) return null;
  const [coefficient, exponentText] = tick.toString().toLowerCase().split('e');
  const exponent = exponentText ? Number(exponentText) : 0;
  const fraction = coefficient.split('.')[1]?.length ?? 0;
  const decimalPlaces = Math.max(0, fraction - exponent);
  return Number.isInteger(decimalPlaces) && decimalPlaces <= 12
    ? decimalPlaces : null;
}

export function contractPriceDecimals(
  specification: InstrumentTickSpecification | null
): number | null {
  if (!specification || !Array.isArray(specification.tickSizeSteps)) return null;
  const sizes = [
    specification.tickSize,
    ...specification.tickSizeSteps.map(step => step.tickSize)
  ];
  const decimals = sizes.map(tickDecimals);
  if (decimals.some(digits => digits === null)) return null;
  return Math.max(...(decimals as number[]));
}

export function formatDomPrice(value: number | null | undefined, decimals: number | null): string {
  if (value === null || value === undefined || !Number.isFinite(value) ||
      decimals === null || !Number.isInteger(decimals) || decimals < 0 || decimals > 12)
    return '—';
  return new Intl.NumberFormat('en-US', {
    minimumFractionDigits: decimals,
    maximumFractionDigits: decimals
  }).format(value);
}

/**
 * Own orders outside the selected, verified public L2 depth window.
 * This does NOT imply that off-screen orders belong to any public level.
 * Orders are displayed with the actual side, price and own remaining size.
 */
export type OwnRestingLevel = {
  direction: 'buy' | 'sell';
  price: number;
  amount: number;
  orders: number;
};
export function ownOrdersOutsideDepth(
  own: OwnRestingLevel[],
  ladder: PriceLadder
): OwnRestingLevel[] {
  const shown = new Set([
    ...ladder.bids.map(level => 'buy:' + level.price),
    ...ladder.asks.map(level => 'sell:' + level.price)
  ]);
  return own
    .filter(item => (item.direction === 'buy' || item.direction === 'sell') &&
      Number.isFinite(item.price) && item.price > 0 &&
      Number.isFinite(item.amount) && item.amount > 0 &&
      Number.isSafeInteger(item.orders) && item.orders > 0 &&
      !shown.has(item.direction + ':' + item.price))
    .sort((a, b) => a.direction === b.direction
      ? b.price - a.price
      : a.direction === 'sell' ? -1 : 1);
}
