/** Pure, exchange-agnostic presentation derivation for already received public trades.
 * No timers, API calls, OHLC extrapolation, or invented prices.
 */
export type VanTradeTick = { price: number | null; timestamp: number | null };
export type VanTickRange = '1m' | '5m' | '15m' | 'all';
export type ValidTradePoint = { price: number; timestamp: number };
export type VanTickSeries = {
  points: readonly ValidTradePoint[];
  low: number | null;
  high: number | null;
  latestTimestamp: number | null;
  polyline: string;
};
const RANGE_DURATION_MS: Record<Exclude<VanTickRange, 'all'>, number> = {
  '1m': 60_000,
  '5m': 300_000,
  '15m': 900_000
};
export function createVanTickSeries(ticks: readonly VanTradeTick[], range: VanTickRange): VanTickSeries {
  const ordered = ticks
    .filter((t): t is ValidTradePoint =>
      t.price !== null && Number.isFinite(t.price) && t.price > 0 &&
      t.timestamp !== null && Number.isFinite(t.timestamp) && t.timestamp > 0)
    .sort((a, b) => a.timestamp - b.timestamp);
  const latest = ordered[ordered.length - 1]?.timestamp;
  if (latest === undefined) {
    return { points: [], low: null, high: null, latestTimestamp: null, polyline: '' };
  }
  // A timeframe is relative to the newest OBSERVED trade, not a claim of
  // market history over that timeframe. The existing tape may contain less.
  const cutoff = range === 'all' ? -Infinity : latest - RANGE_DURATION_MS[range];
  const points = ordered.filter(t => t.timestamp >= cutoff).slice(-120);
  const low = Math.min(...points.map(t => t.price));
  const high = Math.max(...points.map(t => t.price));
  const first = points[0].timestamp;
  const elapsed = latest - first;
  const priceSpan = high - low;
  const polyline = points.map((t, index) => {
    // When simultaneous trades share a timestamp, space points by index
    // instead of generating a degenerate vertical graph.
    const ratio = elapsed > 0
      ? (t.timestamp - first) / elapsed
      : (points.length <= 1 ? 0.5 : index / (points.length - 1));
    const x = 12 + ratio * 456;
    const y = priceSpan > 0 ? 138 - (t.price - low) / priceSpan * 116 : 80;
    return x.toFixed(2) + ',' + y.toFixed(2);
  }).join(' ');
  return { points, low, high, latestTimestamp: latest, polyline };
}
