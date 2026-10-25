import { useMemo, useState, type KeyboardEvent } from 'react';
import { createVanTickSeries, type VanTradeTick, type VanTickRange } from './vanChartSeries';
export type { VanTradeTick } from './vanChartSeries';

/** VAN presentation primitives: no exchange transports or imperative globals. */
export type VanAccountTab = 'orders' | 'positions';
type Currency = 'BTC' | 'ETH';

export function VanNavigation({ instrument, environment, onPerpetualSelect }: {
  instrument: string; environment: 'mainnet' | 'testnet';
  onPerpetualSelect: (currency: Currency) => void;
}) {
  return (
    <nav className="van-navigation" aria-label="Deribit workstation navigation">
      <div className="van-primary-tabs" role="tablist" aria-label="Workspace">
        <button type="button" role="tab" disabled aria-selected={false} title="Markets API not connected">Markets</button>
        <button type="button" role="tab" aria-selected={true} className="active">Trading</button>
        <button type="button" role="tab" disabled aria-selected={false} title="Assets API not connected">Assets</button>
        <span className="van-nav-safe">{environment === 'mainnet' ? 'MAINNET READ-ONLY' : 'TESTNET · EXECUTION GATED'}</span>
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

export function VanTradeChart({ instrument, ticks }: {
  instrument: string; ticks: readonly VanTradeTick[];
}) {
  // Filters *existing* WS tape only. UI timeframes are not OHLC/bar data.
  const [range, setRange] = useState<VanTickRange>('all');
  const series = useMemo(() => createVanTickSeries(ticks, range), [ticks, range]);
  const rising = series.points.length > 1 &&
    series.points[series.points.length - 1].price >= series.points[0].price;
  return (
    <section className="van-tick-chart" aria-label={'Public trade chart ' + instrument}>
      <div className="van-chart-head">
        <strong>Price chart</strong>
        <span>PUBLIC WS · TICKS ONLY</span>
        <div className="van-chart-range" role="group" aria-label="Visible public trade window">
          {(['1m', '5m', '15m', 'all'] as const).map(option =>
            <button type="button" key={option} aria-pressed={range === option}
              className={range === option ? 'active' : ''}
              onClick={() => setRange(option)}>{option === 'all' ? 'All' : option}</button>)}
        </div>
      </div>
      {series.points.length < 2
        ? <div className="van-chart-unavailable" role="status">Недостаточно публичных сделок в выбранном окне · исторические свечи пока недоступны</div>
        : <div className="van-chart-plot">
            <svg viewBox="0 0 480 152" role="img" preserveAspectRatio="none"
              aria-label={instrument + ': ' + series.points.length + ' public trades; price range ' +
                series.low?.toFixed(2) + '–' + series.high?.toFixed(2)}>
              <line x1="0" y1="22" x2="480" y2="22" />
              <line x1="0" y1="80" x2="480" y2="80" />
              <line x1="0" y1="138" x2="480" y2="138" />
              <polyline points={series.polyline} fill="none"
                stroke={rising ? '#45c4a5' : '#f04e60'} strokeWidth="2"
                vectorEffect="non-scaling-stroke" strokeLinejoin="round" />
            </svg>
            <div className="van-chart-price-labels" aria-hidden="true">
              <span>{series.high?.toFixed(2)}</span><span>{series.low?.toFixed(2)}</span>
            </div>
          </div>}
      <p className="van-chart-caption">
        {series.points.length} trades · {range === 'all' ? 'buffer' : range + ' до последней сделки'}
        {' · '}WS buffer, не OHLC · без исторического backfill
      </p>
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
  function onTabKeyDown(event: KeyboardEvent<HTMLButtonElement>, current: VanAccountTab) {
    if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
    event.preventDefault();
    const target: VanAccountTab = event.key === 'Home' ? 'positions'
      : event.key === 'End' ? 'orders'
      : event.key === 'ArrowLeft' ? 'positions' : 'orders';
    if (target !== current) onSelect(target);
    event.currentTarget.parentElement?.querySelector<HTMLButtonElement>(
      '[data-van-tab="' + target + '"]')?.focus();
  }
  return <div className="van-bottom-tabs" role="tablist" aria-label="Account state panels">
    {active.map(tab => <button key={tab.id} id={'van-account-tab-' + tab.id}
      data-van-tab={tab.id} aria-controls={'van-account-panel-' + tab.id}
      type="button" role="tab" tabIndex={selected === tab.id ? 0 : -1}
      aria-selected={selected === tab.id} className={selected === tab.id ? 'active' : ''}
      onKeyDown={event => onTabKeyDown(event, tab.id)}
      onClick={() => onSelect(tab.id)}>{tab.label} ({tab.count ?? '—'})</button>)}
    {['Order history', 'Trade history', 'Position history', 'Funding', 'Exercise', 'Assets'].map(label =>
      <button key={label} type="button" role="tab" aria-selected={false} disabled
        title="Нет подтверждённого API-контракта; данные не имитируются">{label}</button>)}
  </div>;
}
