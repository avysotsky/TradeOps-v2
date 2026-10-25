import { useMemo } from 'react';

/** VAN presentation primitives: no exchange transports or imperative globals. */
export type VanAccountTab = 'orders' | 'positions';
type Currency = 'BTC' | 'ETH';

export function VanNavigation({ instrument, onPerpetualSelect }: {
  instrument: string; onPerpetualSelect: (currency: Currency) => void;
}) {
  return (
    <nav className="van-navigation" aria-label="Deribit workstation navigation">
      <div className="van-primary-tabs" role="tablist" aria-label="Workspace">
        <button type="button" role="tab" disabled aria-selected={false} title="Markets API not connected">Markets</button>
        <button type="button" role="tab" aria-selected={true} className="active">Trading</button>
        <button type="button" role="tab" disabled aria-selected={false} title="Assets API not connected">Assets</button>
        <span className="van-nav-safe">MAINNET READ-ONLY</span>
      </div>
      <div className="van-trade-tabs" role="tablist" aria-label="Trading views">
        <button type="button" role="tab" disabled aria-selected={false} title="Spot workstation not connected">Spot</button>
        <button type="button" role="tab" aria-selected={true} className="active">Futures</button>
        <button type="button" role="tab" disabled aria-selected={false} title="Options chain not connected; instrument selector supports options">Options</button>
        <button type="button" role="tab" disabled aria-selected={false} title="Spreads engine not connected">Spreads</button>
      </div>
      <div className="van-contract-strip" role="group" aria-label="Perpetual contract quick selection">
        {(['BTC', 'ETH'] as const).map(ccy => (
          <button key={ccy} type="button" aria-pressed={instrument === ccy + '-PERPETUAL'}
            className={instrument === ccy + '-PERPETUAL' ? 'active' : ''}
            onClick={() => onPerpetualSelect(ccy)}>{ccy}-PERPETUAL</button>
        ))}
        <span>Read-only public markets · Deribit</span>
      </div>
    </nav>
  );
}

export type VanTradeTick = { price: number | null; timestamp: number | null };
export function VanTradeChart({ instrument, ticks }: {
  instrument: string; ticks: readonly VanTradeTick[];
}) {
  // A truthful public-trade microchart. NOT historical OHLC.
  const points = useMemo(() => ticks
    .filter((t): t is { price: number; timestamp: number } =>
      t.price !== null && Number.isFinite(t.price) && t.price > 0 &&
      t.timestamp !== null && Number.isFinite(t.timestamp))
    .slice(0, 80).sort((a, b) => a.timestamp - b.timestamp), [ticks]);
  const low = Math.min(...points.map(p => p.price));
  const high = Math.max(...points.map(p => p.price));
  const span = Math.max(high - low, Number.EPSILON);
  const polyline = points.map((p, index) => {
    const x = 12 + (points.length === 1 ? 0 : index * 456 / (points.length - 1));
    const y = 138 - (p.price - low) / span * 116;
    return x.toFixed(2) + ',' + y.toFixed(2);
  }).join(' ');
  const rising = points.length > 1 && points[points.length - 1].price >= points[0].price;
  return (
    <section className="van-tick-chart" aria-label={'Live public trade prices ' + instrument}>
      <div className="van-chart-head"><strong>Price chart</strong><span>PUBLIC TRADES · TICK VIEW</span></div>
      {points.length < 2
        ? <div className="van-chart-unavailable" role="status">Ожидание публичных сделок WebSocket · исторические свечи пока недоступны</div>
        : <svg viewBox="0 0 480 152" role="img" preserveAspectRatio="none"
            aria-label={instrument + ': последние ' + points.length + ' публичных сделок; диапазон ' +
              low.toFixed(2) + '–' + high.toFixed(2)}>
            <line x1="0" y1="22" x2="480" y2="22" />
            <line x1="0" y1="80" x2="480" y2="80" />
            <line x1="0" y1="138" x2="480" y2="138" />
            <polyline points={polyline} fill="none" stroke={rising ? '#45c4a5' : '#f04e60'}
              strokeWidth="2" vectorEffect="non-scaling-stroke" strokeLinejoin="round" />
          </svg>}
      <p className="van-chart-caption">Live ticks, не OHLC · текущая публичная лента · без исторического backfill</p>
    </section>
  );
}

export function VanBottomTabs({ selected, onSelect, positionsCount, ordersCount }: {
  selected: VanAccountTab;
  onSelect: (next: VanAccountTab) => void;
  positionsCount: number | null;
  ordersCount: number | null;
}) {
  const active: { id: VanAccountTab; label: string; count: number | null }[] = [
    { id: 'positions', label: 'Positions', count: positionsCount },
    { id: 'orders', label: 'Orders', count: ordersCount }
  ];
  return <div className="van-bottom-tabs" role="tablist" aria-label="Account state panels">
    {active.map(tab => <button key={tab.id} type="button" role="tab"
      aria-selected={selected === tab.id} className={selected === tab.id ? 'active' : ''}
      onClick={() => onSelect(tab.id)}>{tab.label} ({tab.count ?? '—'})</button>)}
    {['Order history', 'Trade history', 'Position history', 'Funding', 'Exercise', 'Assets'].map(label =>
      <button key={label} type="button" role="tab" aria-selected={false} disabled
        title="Нет подтверждённого API-контракта; данные не имитируются">{label}</button>)}
  </div>;
}
