import { useCallback, useEffect, useState } from 'react';
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
  const [status, setStatus] = useState<Status | null>(null);
  const [instrument, setInstrument] = useState('BTC-PERPETUAL');
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

  const refresh = useCallback(async () => {
    if (!key || !status) return;
    setRefreshing(true);
    try {
      const ticker = await api<Quote>(key, '/ticker?instrument=' + encodeURIComponent(instrument));
      setQuote(ticker);
      if (status.credentialsConfigured) {
        const [btc, eth, pos, open] = await Promise.all([
          api<Account>(key, '/account?currency=BTC'),
          api<Account>(key, '/account?currency=ETH'),
          api<Position[]>(key, '/positions'),
          api<Order[]>(key, '/orders')
        ]);
        setAccounts([btc, eth]);
        setPositions(pos);
        setOrders(open);
      }
      setError('');
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setRefreshing(false);
    }
  }, [key, status, instrument]);

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
        <div className="content">
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
          {message && <div className="notice positive"><CheckCircle2 size={18}/> {message}</div>}
          {!status.credentialsConfigured && <div className="notice"><KeyRound size={17}/>
            Биржевые credentials на backend ещё не настроены. Публичные котировки доступны; состояние счёта — нет.
          </div>}

          <div className="main-grid">
            <div className="stack">
              <section className="panel quote-panel">
                <div className="panel-header">
                  <div><span className="eyebrow">MARKET DATA</span><h2>Котировки Deribit</h2></div>
                  <select aria-label="Инструмент" value={instrument}
                    onChange={e => { setInstrument(e.target.value); setPrice(''); setPreview(null); }}>
                    {status.instruments.map(x => <option key={x} value={x}>{x}</option>)}
                  </select>
                </div>
                <div className="price-hero">
                  <div><span>MARK PRICE</span><strong>{num(quote?.markPrice, 2)}</strong></div>
                  <div className="price-sub"><span>Last: {num(quote?.lastPrice, 2)}</span><span>Index: {num(quote?.indexPrice, 2)}</span></div>
                </div>
                <div className="bid-ask">
                  <div><span><ArrowDownRight size={15}/> BEST BID</span><strong>{num(quote?.bestBidPrice, 2)}</strong></div>
                  <div><span><ArrowUpRight size={15}/> BEST ASK</span><strong>{num(quote?.bestAskPrice, 2)}</strong></div>
                </div>
                <div className="panel-foot"><Clock3 size={14}/> Обновление каждые 8 секунд · HTTP polling
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
              <label>Инструмент</label><div className="ticket-value">{instrument}</div>
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

          <section className="panel data-panel">
            <div className="panel-header"><div><span className="eyebrow">EXCHANGE STATE</span><h2>Открытые позиции</h2></div><span className="count">{positions.length} positions</span></div>
            <div className="table-wrap">
              <table><thead><tr><th>Инструмент</th><th>Тип</th><th>Сторона</th><th className="right">Native size</th><th className="right">Mark</th><th className="right">Floating PnL</th></tr></thead>
                <tbody>{positions.length ? positions.map((p, i) => <tr key={p.instrument + i}>
                  <td className="mono strong">{p.instrument}</td><td>{p.kind}</td>
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
