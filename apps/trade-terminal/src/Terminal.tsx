import { useCallback, useEffect, useMemo, useState, type CSSProperties } from 'react';
import { toPriceLadder, domPriceDecimals, formatDomPrice } from './marketDepth';
import {
  Activity, ArrowDownRight, ArrowUpRight, CheckCircle2,
  CircleDollarSign, Clock3, KeyRound, RefreshCw,
  ShieldAlert, ShieldCheck, Terminal, X
} from 'lucide-react';

type Status = {
  environment: 'mainnet' | 'testnet';
  exchange: string;
  credentialsConfigured: boolean;
  tradingEnabled: boolean;
  maxContracts: number;
  instruments: string[];
  localOnly: boolean;
};
type Quote = {
  instrument: string;
  bestBidPrice: number | null;
  bestAskPrice: number | null;
  markPrice: number | null;
  lastPrice: number | null;
  indexPrice: number | null;
  timestampUtc: string;
};
type MarketInstrument = {
  name: string;
  kind: string;
  currency: string;
  active: boolean;
  tickSize: number | null;
  contractSize: number | null;
  minTradeAmount: number | null;
  expirationTimestamp: number | null;
  strike: number | null;
  optionType: string | null;
};
type InstrumentList = {
  currency: string;
  kind: string;
  available: number;
  items: MarketInstrument[];
  cachedUntilUtc: string;
};
type DepthLevel = { price: number; amount: number };
type OrderBook = {
  instrument: string;
  bids: DepthLevel[];
  asks: DepthLevel[];
  markPrice: number | null;
  changeId: number | null;
  timestamp: number | null;
  receivedAtUtc: string;
};
type PublicTrade = {
  tradeId: string | null;
  direction: string | null;
  price: number | null;
  amount: number | null;
  timestamp: number | null;
};
type TradeTape = {
  instrument: string;
  trades: PublicTrade[];
  receivedAtUtc: string;
};
type Account = {
  currency: string;
  equity: number | null;
  balance: number | null;
  availableFunds: number | null;
  marginBalance: number | null;
};
type Position = {
  instrument: string;
  kind: string;
  direction: string;
  nativeSize: number | null;
  markPrice: number | null;
  floatingPnl: number | null;
};
type Order = {
  orderId: string | null;
  instrument: string | null;
  side: string | null;
  state: string | null;
  amount: number | null;
  filledAmount: number | null;
  price: number | null;
  label: string | null;
};
type Preview = {
  previewId: string;
  environment: string;
  instrument: string;
  side: string;
  contracts: number;
  price: number;
  orderType: string;
  postOnly: boolean;
  rejectPostOnly: boolean;
  expiresUtc: string;
  warning: string;
};
type ExecutionResult = {
  environment?: string;
  label?: string;
  orderId?: string;
  state?: string;
  error?: string;
};

const endpoint = '/api/deribit-terminal';

async function api<T>(key: string, route: string, init?: RequestInit): Promise<T> {
  const response = await fetch(endpoint + route, {
    ...init,
    cache: 'no-store',
    headers: {
      ...(init?.body ? { 'Content-Type': 'application/json' } : {}),
      // Read-only GETs are authorized by the Vite server proxy without
      // revealing its ephemeral operator key to the browser.
      ...(init?.method && init.method !== 'GET'
        ? { 'X-TradeOps-Terminal-Key': key } : {})
    }
  });
  const data: unknown = await response.json().catch(() => ({}));
  if (!response.ok) {
    const error = (data && typeof data === 'object' && 'error' in data)
      ? String((data as { error: unknown }).error)
      : 'HTTP ' + response.status;
    throw new Error(error);
  }
  return data as T;
}

function num(value?: number | null, digits = 2) {
  if (value === null || value === undefined || !Number.isFinite(value)) return '—';
  return new Intl.NumberFormat('en-US', { maximumFractionDigits: digits }).format(value);
}

export default function DeribitTerminal() {
  const [key, setKey] = useState('auto-readonly');
  const [tableFontSize, setTableFontSize] = useState(16);
  const [status, setStatus] = useState<Status | null>(null);
  const [instrument, setInstrument] = useState('BTC-PERPETUAL');
  const [marketCurrency, setMarketCurrency] = useState<'BTC' | 'ETH'>('ETH');
  const [marketKind, setMarketKind] = useState<'future' | 'option'>('future');
  const [instrumentSearch, setInstrumentSearch] = useState('');
  const [marketInstrument, setMarketInstrument] = useState('ETH-PERPETUAL');
  const [marketInstruments, setMarketInstruments] = useState<MarketInstrument[]>([]);
  const [universeCount, setUniverseCount] = useState(0);
  const [book, setBook] = useState<OrderBook | null>(null);

  const [tape, setTape] = useState<TradeTape | null>(null);
  const [marketError, setMarketError] = useState('');
  const [quote, setQuote] = useState<Quote | null>(null);
  const [accounts, setAccounts] = useState<Account[]>([]);
  const [positions, setPositions] = useState<Position[]>([]);
  const [orders, setOrders] = useState<Order[]>([]);
  const [side, setSide] = useState<'buy' | 'sell'>('buy');
  const [contracts, setContracts] = useState('10');
  const [price, setPrice] = useState('');
  const [preview, setPreview] = useState<Preview | null>(null);
  const [loading, setLoading] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState('');
  const [message, setMessage] = useState('');

  const connect = async () => {
    setError('');
    setLoading(true);
    try {
      const state = await api<Status>('', '/status');
      if (state.tradingEnabled) {
        throw new Error('Автоматический вход доступен только в режиме просмотра. Отключи execution на backend.');
      }
      setStatus(state);
      setKey('auto-readonly');
      if (!state.instruments.includes(instrument)) setInstrument(state.instruments[0]);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    void connect();
    // Only initial automatic read-only session bootstrap; no password input.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Read-only instrument discovery is cached server-side for 60s and
  // debounced here so option-chain searching doesn't hit Deribit per keystroke.
  useEffect(() => {
    if (!key || !status) return;
    let cancelled = false;
    const task = window.setTimeout(() => {
      const query = new URLSearchParams({
        currency: marketCurrency,
        kind: marketKind,
        limit: '100',
        search: instrumentSearch.trim()
      });
      void api<InstrumentList>(key, '/market/instruments?' + query).then(result => {
        if (!cancelled) {
          setMarketInstruments(result.items);
          setUniverseCount(result.available);
        }
      }).catch((e: Error) => {
        if (!cancelled) {
          setMarketInstruments([]);
          setMarketError(e.message);
        }
      });
    }, 400);
    return () => { cancelled = true; window.clearTimeout(task); };
  }, [key, status, marketCurrency, marketKind, instrumentSearch]);

  useEffect(() => {
    setQuote(null);
    setBook(null);
    setTape(null);
  }, [marketInstrument]);

  const [streamState, setStreamState] = useState('connecting');
  const dom = useMemo(
    () => toPriceLadder(streamState === 'synced' && book?.instrument === marketInstrument ? book : null),
    [book, marketInstrument, streamState]
  );
  const priceDecimals = useMemo(() => {
    const metadata = marketInstruments.find(item => item.name === marketInstrument);
    // The marketKind filter can change before instrument selection, so
    // derive kind from the SELECTED symbol, not from pending search filters.
    const selectedKind = metadata?.kind === 'option' || /-[CP]$/.test(marketInstrument)
      ? 'option' : 'future';
    return domPriceDecimals(dom, metadata?.tickSize, selectedKind);
  }, [dom, marketInstruments, marketInstrument]);
  const [exchangeAgeMs, setExchangeAgeMs] = useState<number | null>(null);
  const [streamMessages, setStreamMessages] = useState(0);
  // ONE continuous WebSocket for selected instrument: NO 8-second market
  // polling. Backend processes every raw L2 delta; only React paints are
  // coalesced to requestAnimationFrame, without holding up market ingestion.
  useEffect(() => {
    if (!key || !status) return;
    let active = true;
    let socket: WebSocket | null = null;
    let reconnect: number | undefined;
    let frame: number | null = null;
    let retryMs = 400;
    const updates: {
      book?: OrderBook;
      ticker?: Quote;
      trades?: TradeTape;
      state?: string;
      lag?: number | null;
      sequence?: number;
    } = {};
    setBook(null);
    setTape(null);
    setQuote(null);
    setStreamState('connecting');
    setExchangeAgeMs(null);

    function renderLatest() {
      frame = null;
      if (!active) return;
      if (updates.state !== undefined) {
        setStreamState(updates.state);
        if (updates.state !== 'synced') {
          setBook(null);
          setTape(null);
        }
      }
      if (updates.book) setBook(updates.book);
      if (updates.ticker) setQuote(updates.ticker);
      if (updates.trades) setTape(updates.trades);
      if (updates.lag !== undefined) setExchangeAgeMs(updates.lag);
      if (updates.sequence !== undefined) setStreamMessages(updates.sequence);
      updates.book = undefined;
      updates.ticker = undefined;
      updates.trades = undefined;
      updates.state = undefined;
      updates.lag = undefined;
      updates.sequence = undefined;
    }
    function queueRender() {
      if (frame === null) frame = window.requestAnimationFrame(renderLatest);
    }
    function open() {
      if (!active) return;
      setStreamState('connecting');
      const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
      const url = protocol + '//' + location.host +
        '/api/deribit-terminal/market/stream?instrument=' +
        encodeURIComponent(marketInstrument);
      socket = new WebSocket(url);
      socket.onopen = () => { retryMs = 400; };
      socket.onmessage = event => {
        if (!active) return;
        let payload: any;
        try { payload = JSON.parse(String(event.data)); }
        catch { socket?.close(); return; }
        if (payload.instrument !== marketInstrument) return;
        if (payload.type === 'status') {
          updates.state = String(payload.state);
          // Drop older queued prices as soon as stream is not synced.
          if (payload.state !== 'synced') {
            updates.book = undefined;
            updates.ticker = undefined;
            updates.trades = undefined;
          }
        } else if (payload.type === 'book' && payload.state === 'synced') {
          const b = payload.book as {
            instrument: string;
            bids: DepthLevel[];
            asks: DepthLevel[];
            changeId: number | null;
            exchangeTimestamp: number | null;
            localReceivedAtUnixMs: number;
          };
          updates.book = {
            instrument: b.instrument,
            bids: b.bids,
            asks: b.asks,
            changeId: b.changeId,
            markPrice: null,
            timestamp: b.exchangeTimestamp,
            receivedAtUtc: new Date(b.localReceivedAtUnixMs).toISOString()
          };
          updates.state = 'synced';
          updates.lag = typeof payload.exchangeAgeMs === 'number' ?
            payload.exchangeAgeMs : null;
          updates.sequence = Number(payload.messages) || 0;
        } else if (payload.type === 'ticker') {
          updates.ticker = payload as Quote;
        } else if (payload.type === 'trades') {
          updates.trades = payload as TradeTape;
        }
        queueRender();
      };
      socket.onclose = () => {
        if (!active) return;
        updates.state = 'disconnected';
        updates.book = undefined;
        updates.ticker = undefined;
        updates.trades = undefined;
        queueRender();
        reconnect = window.setTimeout(open, retryMs);
        retryMs = Math.min(retryMs * 2, 10000);
      };
      socket.onerror = () => { socket?.close(); };
    }
    open();
    return () => {
      active = false;
      if (reconnect !== undefined) window.clearTimeout(reconnect);
      if (frame !== null) window.cancelAnimationFrame(frame);
      if (socket) {
        socket.onclose = null;
        socket.onmessage = null;
        socket.close();
      }
    };
  }, [key, status, marketInstrument]);

  // Private account/positions/orders are not market data: their slower
  // periodic refresh is independent of the raw exchange feed.
  const refresh = useCallback(async () => {
    if (!key || !status) return;
    setRefreshing(true);
    if (status.credentialsConfigured) {
      const accountResults = await Promise.allSettled([
        api<Account>(key, '/account?currency=BTC'),
        api<Account>(key, '/account?currency=ETH'),
        api<Position[]>(key, '/positions'),
        api<Order[]>(key, '/orders')
      ]);
      const [btc, eth, pos, open] = accountResults;
      if (btc.status === 'fulfilled' && eth.status === 'fulfilled')
        setAccounts([btc.value, eth.value]);
      if (pos.status === 'fulfilled') setPositions(pos.value);
      if (open.status === 'fulfilled') setOrders(open.value);
      setError(accountResults.some(r => r.status === 'rejected') ?
        'Часть данных счёта Deribit сейчас недоступна.' : '');
    }
    setRefreshing(false);
  }, [key, status]);

  useEffect(() => {
    if (!key || !status) return;
    void refresh();
    const interval = window.setInterval(() => { void refresh(); }, 8000);
    return () => window.clearInterval(interval);
  }, [key, status, refresh]);

  const previewOrder = async () => {
    setError('');
    setMessage('');
    if (!status?.tradingEnabled) {
      setError('Режим исполнения выключен на сервере.');
      return;
    }
    setLoading(true);
    try {
      const v = await api<Preview>(key, '/orders/preview', {
        method: 'POST',
        body: JSON.stringify({
          instrument,
          side,
          contracts: Number(contracts),
          price: Number(price)
        })
      });
      setPreview(v);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoading(false);
    }
  };

  const confirmOrder = async () => {
    if (!preview) return;
    const id = preview.previewId;
    setPreview(null); // one-shot confirmation; no automatic retry on failure
    setLoading(true);
    setError('');
    try {
      const r = await api<ExecutionResult>(key, '/orders/confirm', {
        method: 'POST', body: JSON.stringify({ previewId: id })
      });
      setMessage('Заявка отправлена. Deribit Order ID: ' + (r.orderId || '—') +
        ', состояние: ' + (r.state || 'неизвестно'));
      await refresh();
    } catch (e) {
      setError((e as Error).message + ' Проверьте ордера на Deribit перед повторной заявкой.');
    } finally {
      setLoading(false);
    }
  };

  const cancelOrder = async (orderId?: string | null) => {
    if (!orderId || !status?.tradingEnabled) return;
    if (!window.confirm('Отменить именно эту заявку Deribit?\n' + orderId)) return;
    setLoading(true);
    setError('');
    try {
      await api(key, '/orders/cancel', {
        method: 'POST',
        body: JSON.stringify({ orderId, confirmation: 'CANCEL ' + orderId })
      });
      setMessage('Запрос на отмену отправлен. Проверьте фактическое состояние ордера.');
      await refresh();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoading(false);
    }
  };

  return (
    <main className="terminal">
      <header className="topbar">
        <div className="branding"><span className="brand-logo"><Activity size={24}/></span>
          <div><strong>TradeOps</strong><span>DERIBIT TERMINAL · v0.1</span></div></div>
        <div className="topbar-right">
          <span className="top-label"><span className="live-dot"/> PRIVATE LOCAL WORKSTATION</span>
          {status && <span className={'environment ' + (status.environment === 'mainnet' ? 'live' : '')}>
            {status.environment === 'mainnet' ? 'MAINNET · REAL FUNDS' : 'TESTNET'}
          </span>}

        </div>
      </header>

      {!key || !status ? (
        <section className="connect-screen">
          <div className="connect-card">
            <span className="eyebrow">LOCAL OPERATOR ACCESS</span>
            <h1>Deribit Trading Terminal</h1>
            <p>Вход автоматически авторизуется локальным TradeOps launcher. Никакого пароля оператора вводить не нужно, а биржевые ключи остаются в памяти backend.</p>
            <button className="primary full" disabled={loading} onClick={() => void connect()}>
              {loading ? 'Подключаемся…' : 'Повторить подключение'} <ArrowUpRight size={16}/>
            </button>
            {error && <p className="error">{error}</p>}
            <p className="footnote"><ShieldCheck size={15}/> Автоматическая сессия только для просмотра · localhost</p>
          </div>
        </section>
      ) : (
        <div className="content" style={{ '--table-font-size': `${tableFontSize}px` } as CSSProperties}>
          <section className="headline">
            <div><span className="eyebrow">TRADING WORKSPACE / DERIBIT</span>
              <h1>Trading overview</h1>
              <p>Реальные котировки биржи · аккаунт и позиции по отдельному API-доступу</p></div>
            <button className="refresh" disabled={refreshing} onClick={() => void refresh()}>
              <RefreshCw size={17} className={refreshing ? 'spin' : ''}/> Обновить
            </button>
          </section>

          <div className="summary">
            <div className="stat"><span>Биржа</span><strong>Deribit</strong><small>{status.environment === 'mainnet' ? 'Production' : 'Testing environment'}</small></div>
            <div className="stat"><span>Торговля</span><strong className={status.tradingEnabled ? 'success-text' : 'muted'}>
              {status.tradingEnabled ? 'Enabled' : 'Disabled'}</strong><small>{status.tradingEnabled ? 'Только ручное подтверждение' : 'Только просмотр'}</small></div>
            <div className="stat"><span>Открытые заявки</span><strong>{status.credentialsConfigured ? orders.length : '—'}</strong><small>На бирже</small></div>
            <div className="stat"><span>Открытые позиции</span><strong>{status.credentialsConfigured ? positions.length : '—'}</strong><small>Все типы инструментов</small></div>
          </div>

          {error && <div className="notice danger"><ShieldAlert size={18}/> {error}</div>}
          {marketError && <div className="notice danger"><ShieldAlert size={18}/> {marketError}</div>}
          {message && <div className="notice positive"><CheckCircle2 size={18}/> {message}</div>}
          {!status.credentialsConfigured && <div className="notice"><KeyRound size={17}/>
            Биржевые credentials на backend ещё не настроены. Публичные котировки доступны; состояние счёта — нет.
          </div>}

          <div className="main-grid">
            <div className="stack">
              <section className="panel quote-panel">
                <div className="panel-header">
                  <div><span className="eyebrow">MARKET DATA</span><h2>Котировки Deribit</h2></div>
                  <select aria-label="Рыночный инструмент" value={marketInstrument}
                    onChange={e => setMarketInstrument(e.target.value)}>
                    {!marketInstruments.some(x => x.name === marketInstrument) &&
                      <option value={marketInstrument}>{marketInstrument}</option>}
                    {marketInstruments.map(x => <option key={x.name} value={x.name}>{x.name}</option>)}
                  </select>
                </div>
                <div className="market-filters">
                  <label>Базовая валюта
                    <select aria-label="Базовая валюта" value={marketCurrency}
                      onChange={e => {
                        const ccy = e.target.value as 'BTC' | 'ETH';
                        setMarketCurrency(ccy);
                        setMarketInstrument(ccy + '-PERPETUAL');
                        setInstrumentSearch('');
                      }}>
                      <option value="ETH">ETH</option><option value="BTC">BTC</option>
                    </select>
                  </label>
                  <label>Тип инструмента
                    <select aria-label="Тип рыночного инструмента" value={marketKind}
                      onChange={e => {
                        setMarketKind(e.target.value as 'future' | 'option');
                        setInstrumentSearch('');
                      }}>
                      <option value="future">Фьючерсы</option><option value="option">Опционы</option>
                    </select>
                  </label>
                  <label>Поиск
                    <input aria-label="Поиск инструмента" maxLength={60} type="text"
                      value={instrumentSearch}
                      onChange={e => setInstrumentSearch(e.target.value.toUpperCase().replace(/[^A-Z0-9_-]/g, ''))}
                      placeholder="25DEC26, 3000-C…" />
                  </label>
                </div>
                <div className="instrument-meta">
                  <span>Выбран: <strong>{marketInstrument}</strong></span>
                  <span>Найдено: {universeCount}</span>
                  {marketKind === 'option' && <span>Цены опционов — в валюте котировки контракта</span>}
                </div>
                <div className="price-hero">
                  <div><span>MARK PRICE</span><strong>{num(quote?.instrument === marketInstrument ? quote.markPrice : null, marketKind === 'option' ? 6 : 2)}</strong></div>
                  <div className="price-sub"><span>Last: {num(quote?.instrument === marketInstrument ? quote.lastPrice : null, marketKind === 'option' ? 6 : 2)}</span><span>Index: {num(quote?.instrument === marketInstrument ? quote.indexPrice : null, 2)}</span></div>
                </div>
                <div className="bid-ask">
                  <div><span><ArrowDownRight size={15}/> BEST BID</span><strong>{num(quote?.instrument === marketInstrument ? quote.bestBidPrice : null, marketKind === 'option' ? 6 : 2)}</strong></div>
                  <div><span><ArrowUpRight size={15}/> BEST ASK</span><strong>{num(quote?.instrument === marketInstrument ? quote.bestAskPrice : null, marketKind === 'option' ? 6 : 2)}</strong></div>
                </div>
                <div className="panel-foot"><Clock3 size={14}/> Рыночные данные: 8 сек · HTTP · без выставления ордеров
                  {quote && <span>{new Date(quote.timestampUtc).toLocaleTimeString('ru-RU')}</span>}
                </div>
              </section>

              <section className="panel">
                <div className="panel-header"><div><span className="eyebrow">PORTFOLIO</span><h2>Состояние счёта</h2></div><CircleDollarSign size={19}/></div>
                <div className="account-grid">
                  {accounts.length ? accounts.map(a => <div className="account-card" key={a.currency}>
                    <span className="currency">{a.currency}</span>
                    <span className="caption">EQUITY</span><strong>{num(a.equity, 6)}</strong>
                    <div><span>Balance</span><b>{num(a.balance, 6)}</b></div>
                    <div><span>Available funds</span><b>{num(a.availableFunds, 6)}</b></div>
                  </div>) : <div className="empty">Счета появятся после настройки серверных API-ключей.</div>}
                </div>
              </section>
            </div>

            <section className="panel ticket">
              <div className="panel-header">
                <div><span className="eyebrow">MANUAL EXECUTION</span><h2>Новая заявка</h2></div>
                <span className="ticket-label">LIMIT · POST-ONLY</span>
              </div>
              <div className="side-selector">
                <button className={side === 'buy' ? 'active-buy' : ''} onClick={() => setSide('buy')}>Buy / Long</button>
                <button className={side === 'sell' ? 'active-sell' : ''} onClick={() => setSide('sell')}>Sell / Short</button>
              </div>
              <label htmlFor="ticket-instrument">Инструмент заявки (отдельно от стакана)</label>
              <select id="ticket-instrument" className="ticket-value" value={instrument}
                onChange={e => { setInstrument(e.target.value); setPrice(''); setPreview(null); }}>
                {status.instruments.map(x => <option key={x} value={x}>{x}</option>)}
              </select>
              <label htmlFor="contracts">Количество контрактов</label>
              <input id="contracts" type="number" min="0.01" step="any" max={status.maxContracts}
                value={contracts} onChange={e => setContracts(e.target.value)} />
              <label htmlFor="price">Лимитная цена (USD)</label>
              <input id="price" type="number" min="0.01" step="0.5"
                placeholder="Введите цену" value={price} onChange={e => setPrice(e.target.value)} />
              <div className="execution-details">
                <div><span>Тип</span><strong>Limit / maker-only</strong></div>
                <div><span>Макс. количество</span><strong>{num(status.maxContracts)}</strong></div>
                <div><span>Среда исполнения</span><strong>{status.environment.toUpperCase()}</strong></div>
                <div><span>Защита</span><strong>Reject if crossing</strong></div>
              </div>
              <button className={side === 'buy' ? 'buy-button' : 'sell-button'}
                disabled={!status.tradingEnabled || !status.credentialsConfigured || loading ||
                  Number(contracts) <= 0 || Number(price) <= 0}
                onClick={() => void previewOrder()}>
                {status.tradingEnabled ? 'Проверить заявку' : 'Исполнение заблокировано'} <ArrowUpRight size={18}/>
              </button>
              <p className="ticket-warning"><ShieldCheck size={16}/>
                {status.tradingEnabled
                  ? 'Никаких автоматических заявок. После проверки потребуется отдельное подтверждение.'
                  : 'Включение торговли требует изменения защищённых настроек backend.'}</p>
            </section>
          </div>

          <section className="market-feed-grid">
            <section className="panel market-feed">
              <div className="panel-header">
                <div><span className="eyebrow">LIVE RAW WEBSOCKET / EVENT-DRIVEN</span><h2>Стакан заявок</h2></div>
                <div className="stream-status" aria-live="polite">
                  <strong className={streamState === 'synced' ? 'success-text' : 'red-text'}>
                    {streamState === 'synced' ? 'LIVE RAW' : streamState.toUpperCase()}
                  </strong>
                  <span>{marketInstrument}</span>
                  {streamState === 'synced' && <span>Δ {exchangeAgeMs === null ? '—' : exchangeAgeMs + ' ms'}*</span>}
                </div>
              </div>
              <div className="dom-ladder" aria-label={'Вертикальный стакан ' + marketInstrument}>
                <div className="dom-head">
                  <span>Цена</span><span>Объём</span><span>Совокупный</span>
                </div>
                <div className="dom-half dom-asks" aria-label="Asks — продавцы сверху">
                  {dom.asks.map(level => (
                    <div className={'dom-row ask' + (level.best ? ' best' : '')}
                      key={'ask-' + level.price}
                      aria-label={'Ask ' + level.price + ', объём ' + level.amount +
                        ', накоплено ' + level.cumulative}>
                      <span className="dom-bar ask-bar" style={{ width: level.percent + '%' }} aria-hidden="true" />
                      <strong className="dom-price">{formatDomPrice(level.price, priceDecimals)}</strong>
                      <span className="dom-size">{num(level.amount, 6)}</span>
                      <span className="dom-cumulative">{num(level.cumulative, 6)}</span>
                    </div>
                  ))}
                  {dom.asks.length === 0 && <p className="dom-empty">Ожидаем синхронизацию Ask</p>}
                </div>
                <div className="dom-mid" aria-label="Спред и средняя цена между лучшими котировками">
                  {dom.spread ? (
                    <>
                      <span className="dom-mid-label">SPREAD</span>
                      <strong className="dom-mid-price">{formatDomPrice(dom.spread.mid, priceDecimals)}</strong>
                      <span className="dom-mid-spread">
                        {formatDomPrice(dom.spread.absolute, priceDecimals)}
                        <span className="dom-bps"> · {num(dom.spread.bps, 2)} bps</span>
                      </span>
                    </>
                  ) : <strong className="dom-mid-unsynced">НЕТ ПОДТВЕРЖДЁННОГО СПРЕДА</strong>}
                </div>
                <div className="dom-half dom-bids" aria-label="Bids — покупатели снизу">
                  {dom.bids.map(level => (
                    <div className={'dom-row bid' + (level.best ? ' best' : '')}
                      key={'bid-' + level.price}
                      aria-label={'Bid ' + level.price + ', объём ' + level.amount +
                        ', накоплено ' + level.cumulative}>
                      <span className="dom-bar bid-bar" style={{ width: level.percent + '%' }} aria-hidden="true" />
                      <strong className="dom-price">{formatDomPrice(level.price, priceDecimals)}</strong>
                      <span className="dom-size">{num(level.amount, 6)}</span>
                      <span className="dom-cumulative">{num(level.cumulative, 6)}</span>
                    </div>
                  ))}
                  {dom.bids.length === 0 && <p className="dom-empty">Ожидаем синхронизацию Bid</p>}
                </div>
              </div>
              <div className="dom-legend">
                <span><i className="dom-legend-swatch ask" /> ASK — предложения на продажу</span>
                <span><i className="dom-legend-swatch bid" /> BID — заявки на покупку</span>
              </div>
              <p className="feed-note">
                Raw L2 snapshot + каждое изменение без 8-секундного polling.
                Последний change_id: {book?.changeId ?? '—'}, сообщений: {streamMessages}.
                * Δ = время сообщения Deribit → локальные часы; требуется синхронизация времени Windows.
                Объёмы в исходных единицах Deribit.
              </p>
            </section>
            <section className="panel market-feed">
              <div className="panel-header">
                <div><span className="eyebrow">LIVE PUBLIC TRADE STREAM</span><h2>Последние сделки</h2></div>
                <span className="count">{tape?.instrument === marketInstrument ? tape.trades.length : 0}</span>
              </div>
              <div className="tape-header"><span>Время</span><span>Цена</span><span>Amount</span></div>
              {(tape?.instrument === marketInstrument ? tape.trades : []).map((x, i) =>
                <div className="tape-row" key={x.tradeId || i}>
                  <span>{x.timestamp ? new Date(x.timestamp).toLocaleTimeString('ru-RU') : '—'}</span>
                  <strong className={x.direction === 'buy' ? 'success-text' : 'red-text'}>{num(x.price, 6)}</strong>
                  <span>{num(x.amount, 5)}</span>
                </div>)}
              {!tape && <p className="empty-feed">Ожидание новых сделок из WebSocket…</p>}
              <p className="feed-note">Это публичные сделки рынка, не история твоего счёта.</p>
            </section>
          </section>

          <section className="panel data-panel">
            <div className="panel-header">
              <div><span className="eyebrow">EXCHANGE STATE</span><h2>Открытые позиции</h2></div>
              <div className="table-controls">
                <span className="count">{positions.length} positions</span>
                <div className="font-controls" role="group" aria-label="Размер шрифта таблиц">
                  <button type="button" aria-label="Уменьшить шрифт таблиц" title="Уменьшить шрифт"
                    disabled={tableFontSize <= 14}
                    onClick={() => setTableFontSize(size => Math.max(14, size - 2))}>A−</button>
                  <span aria-live="polite">{tableFontSize} px</span>
                  <button type="button" aria-label="Увеличить шрифт таблиц" title="Увеличить шрифт"
                    disabled={tableFontSize >= 20}
                    onClick={() => setTableFontSize(size => Math.min(20, size + 2))}>A+</button>
                </div>
              </div>
            </div>
            <div className="table-wrap">
              <table><thead><tr><th>Инструмент</th><th>Тип</th><th>Сторона</th><th className="right">Native size</th><th className="right">Mark</th><th className="right">Floating PnL</th></tr></thead>
                <tbody>{positions.length ? positions.map((p, i) => <tr key={p.instrument + i}>
                  <td className="mono strong">
                    <button className="position-market-link" type="button"
                      title="Открыть этот контракт в стакане"
                      onClick={() => {
                        setMarketInstrument(p.instrument);
                        setMarketCurrency(p.instrument.startsWith('BTC') ? 'BTC' : 'ETH');
                        setMarketKind(p.kind === 'option' ? 'option' : 'future');
                        setInstrumentSearch('');
                      }}>{p.instrument}</button>
                  </td><td>{p.kind}</td>
                  <td className={p.direction === 'buy' ? 'success-text' : 'red-text'}>{p.direction}</td>
                  <td className="right mono">{num(p.nativeSize, 8)}</td>
                  <td className="right mono">{num(p.markPrice)}</td>
                  <td className={'right mono ' + ((p.floatingPnl || 0) >= 0 ? 'success-text' : 'red-text')}>{num(p.floatingPnl, 8)}</td>
                </tr>) : <tr><td colSpan={6} className="empty-cell">Нет позиций или данные счёта не загружены.</td></tr>}</tbody></table>
            </div>
          </section>

          <section className="panel data-panel">
            <div className="panel-header"><div><span className="eyebrow">ORDER MANAGEMENT</span><h2>Активные ордера</h2></div><span className="count">{orders.length} orders</span></div>
            <div className="table-wrap"><table><thead><tr><th>Инструмент</th><th>Order ID</th><th>Сторона</th><th className="right">Объём</th><th className="right">Цена</th><th>Состояние</th><th></th></tr></thead>
              <tbody>{orders.length ? orders.map((o, i) => <tr key={o.orderId || i}>
                <td className="mono strong">{o.instrument}</td>
                <td className="mono muted truncate-cell" title={o.orderId || ''}>{o.orderId || '—'}</td>
                <td className={o.side === 'buy' ? 'success-text' : 'red-text'}>{o.side}</td>
                <td className="right mono">{num(o.amount, 6)}</td><td className="right mono">{num(o.price)}</td>
                <td><span className="state-pill">{o.state || '—'}</span></td>
                <td className="right"><button className="cancel" disabled={!status.tradingEnabled || loading || !o.orderId}
                  onClick={() => void cancelOrder(o.orderId)}>Cancel</button></td>
              </tr>) : <tr><td colSpan={7} className="empty-cell">Открытых ордеров нет или данные недоступны.</td></tr>}</tbody></table></div>
          </section>

          <footer className="footer"><span><Terminal size={14}/> TradeOps · Manual Deribit Terminal</span>
            <span>Не выполняет автоматических сделок · Только локальный доступ</span></footer>
        </div>
      )}

      {preview && <div className="modal-backdrop" role="presentation">
        <div className="confirm-modal" role="dialog" aria-modal="true" aria-label="Подтверждение заявки">
          <div className="modal-icon"><ShieldAlert size={23}/></div>
          <span className="eyebrow">FINAL ORDER CONFIRMATION</span>
          <h2>{preview.environment === 'mainnet' ? 'Реальная заявка на Deribit' : 'Тестовая заявка на Deribit'}</h2>
          <p>Подтверди параметры перед отправкой. Цена и объём будут зафиксированы на сервере; ордер будет post-only.</p>
          <div className="confirm-data">
            <div><span>Среда</span><strong className={preview.environment === 'mainnet' ? 'red-text' : ''}>{preview.environment.toUpperCase()}</strong></div>
            <div><span>Направление</span><strong>{preview.side.toUpperCase()}</strong></div>
            <div><span>Инструмент</span><strong>{preview.instrument}</strong></div>
            <div><span>Контракты</span><strong>{num(preview.contracts, 8)}</strong></div>
            <div><span>Цена</span><strong>{num(preview.price, 2)} USD</strong></div>
            <div><span>Исполнение</span><strong>LIMIT · POST-ONLY</strong></div>
          </div>
          <div className="modal-actions">
            <button className="secondary" onClick={() => setPreview(null)}><X size={16}/> Отмена</button>
            <button className={preview.environment === 'mainnet' ? 'danger-button' : 'primary'}
              disabled={loading} onClick={() => void confirmOrder()}>
              {preview.environment === 'mainnet' ? 'Разместить РЕАЛЬНЫЙ ордер' : 'Разместить тестовый ордер'}
            </button>
          </div>
        </div>
      </div>}
    </main>
  );
}
