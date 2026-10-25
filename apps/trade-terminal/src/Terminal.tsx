import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties } from 'react';
import { toPriceLadder, ownOrdersOutsideDepth, contractPriceDecimals, formatDomPrice, type DomDepth, type InstrumentTickSpecification } from './marketDepth';
import {
  PrivateReadGate, emptyReadSnapshot, settleReadSnapshot, settleAccountPair, snapshotCount,
  unavailableReadSnapshot, type ReadSnapshot
} from './privateReadState';
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
type ExchangeClockSample = {
  status: 'ok';
  clockOffsetMs: number;
  rttMs: number;
  sampleUnixMs: number;
};
type ManualClockCheck = ExchangeClockSample & {
  rawDeltaAtCheckMs: number | null;
};
type FeedDiagnostics = {
  publicL2State: string;
  privateOrdersState: string;
  requestedDepth: number;
  exchangeChannel: string;
  exchangeMode: string;
  reasonCode: string | null;
  failureAtUtc: string | null;
  expectedChangeId: number | null;
  prevChangeId: number | null;
  changeId: number | null;
  reconnectAttempts: number;
  counters: Record<string, number>;
};
type OwnOrderLevel = {
  direction: 'buy' | 'sell';
  price: number;
  amount: number;
  orders: number;
};
type OwnOrders = {
  instrument: string;
  state: 'loading' | 'ready' | 'unavailable' | 'resyncing' | 'disconnected';
  levels: OwnOrderLevel[];
  openOrders: number;
  snapshotUtcMs: number;
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

// REST snapshots must have the expected shape before a section is marked ready.
function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}
function isAccount(value: unknown): value is Account {
  return isRecord(value) && (value.currency === 'BTC' || value.currency === 'ETH');
}
function isPosition(value: unknown): value is Position {
  return isRecord(value) && typeof value.instrument === 'string' &&
    typeof value.kind === 'string' && typeof value.direction === 'string';
}
function isOrder(value: unknown): value is Order {
  return isRecord(value) &&
    (typeof value.orderId === 'string' || value.orderId === null) &&
    (typeof value.instrument === 'string' || value.instrument === null) &&
    (typeof value.state === 'string' || value.state === null);
}

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
  const [contractSpec, setContractSpec] = useState<InstrumentTickSpecification | null>(null);
  const [contractSpecError, setContractSpecError] = useState('');
  const [universeCount, setUniverseCount] = useState(0);
  const [book, setBook] = useState<OrderBook | null>(null);
  const [domDepth, setDomDepth] = useState<DomDepth>('raw');
  const ladderScrollRef = useRef<HTMLDivElement | null>(null);
  const ladderCenterRef = useRef<HTMLDivElement | null>(null);
  const [ownOrders, setOwnOrders] = useState<OwnOrders | null>(null);

  const [tape, setTape] = useState<TradeTape | null>(null);
  const [marketError, setMarketError] = useState('');
  const [quote, setQuote] = useState<Quote | null>(null);
  const [accountSnapshot, setAccountSnapshot] = useState<ReadSnapshot<Account>>(emptyReadSnapshot);
  const [positionsSnapshot, setPositionsSnapshot] = useState<ReadSnapshot<Position>>(emptyReadSnapshot);
  const [ordersSnapshot, setOrdersSnapshot] = useState<ReadSnapshot<Order>>(emptyReadSnapshot);
  const accounts = accountSnapshot.items;
  const positions = positionsSnapshot.items;
  const orders = ordersSnapshot.items;
  const privateReadGate = useRef(new PrivateReadGate());
  const pendingPrivateAbort = useRef<AbortController | null>(null);
  const [privateReadError, setPrivateReadError] = useState('');
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
  const ownLevels = useMemo(() => {
    const map = new Map<string, OwnOrderLevel>();
    if (streamState === 'synced' && ownOrders?.instrument === marketInstrument &&
        ownOrders.state === 'ready') {
      for (const level of ownOrders.levels)
        map.set(level.direction + ':' + level.price, level);
    }
    return map;
  }, [marketInstrument, ownOrders, streamState]);
  const dom = useMemo(
    () => toPriceLadder(streamState === 'synced' && book?.instrument === marketInstrument ? book : null, domDepth),
    [book, marketInstrument, streamState, domDepth]
  );
  const outsideOwnOrders = useMemo(
    () => streamState === 'synced' && ownOrders?.instrument === marketInstrument &&
      ownOrders.state === 'ready'
      ? ownOrdersOutsideDepth(ownOrders.levels, dom)
      : [],
    [ownOrders, marketInstrument, streamState, dom]
  );
  // Center best bid/ask around Last/Mark/Index on depth switches or first
  // valid snapshot. Do not re-center on each live RAW book update: that would
  // fight operator scrolling when the selected depth or RAW mode is tall.
  const ladderReady = streamState === 'synced' &&
    dom.asks.length > 0 && dom.bids.length > 0;
  useEffect(() => {
    if (!ladderReady) return;
    const scroll = ladderScrollRef.current;
    const center = ladderCenterRef.current;
    if (!scroll || !center) return;
    scroll.scrollTop = Math.max(0,
      center.offsetTop - (scroll.clientHeight - center.clientHeight) / 2);
  }, [domDepth, marketInstrument, ladderReady]);

  // Display precision is fixed by the selected instrument specification.
  // Never derive a number of decimals from spread or observed market prices.
  const priceDecimals = useMemo(
    () => contractPriceDecimals(
      contractSpec?.name === marketInstrument ? contractSpec : null
    ),
    [contractSpec, marketInstrument]
  );

  useEffect(() => {
    if (!key || !status) return;
    let active = true;
    let retry: number | undefined;
    const abort = new AbortController();
    setContractSpec(null);
    setContractSpecError('');
    async function loadSpecification(attempt = 0): Promise<void> {
      try {
        const result = await api<InstrumentTickSpecification>(key,
          '/market/instrument?instrument=' + encodeURIComponent(marketInstrument),
          { signal: abort.signal });
        if (active) {
          setContractSpec(result);
          setContractSpecError('');
        }
      } catch (e) {
        if (!active || abort.signal.aborted) return;
        setContractSpecError('Не удалось загрузить спецификацию ценового шага Deribit.');
        if (attempt < 2) {
          retry = window.setTimeout(() => { void loadSpecification(attempt + 1); }, 2000);
        }
      }
    }
    void loadSpecification();
    return () => {
      active = false;
      abort.abort();
      if (retry !== undefined) window.clearTimeout(retry);
    };
  }, [key, status, marketInstrument]);

  const [exchangeAgeMs, setExchangeAgeMs] = useState<number | null>(null);
  const [exchangeClock, setExchangeClock] = useState<ManualClockCheck | null>(null);
  const [clockProbePending, setClockProbePending] = useState(false);
  const [clockProbeError, setClockProbeError] = useState('');
  const [streamMessages, setStreamMessages] = useState(0);
  const [feedDiagnostics, setFeedDiagnostics] = useState<FeedDiagnostics | null>(null);
  const [clientWsDisconnects, setClientWsDisconnects] = useState(0);
  // ONE WebSocket for selected instrument AND depth: changes trigger a fresh
  // exchange subscription. Explicit RAW remains full depth upstream;
  // grouped depth 1/10/20 arrives as complete 100ms snapshots.
  // React paints are coalesced without holding up market ingestion.
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
      ownOrders?: OwnOrders;
      diagnostics?: FeedDiagnostics;
    } = {};
    setFeedDiagnostics(null);
    setClientWsDisconnects(0);
    setBook(null);
    setTape(null);
    setQuote(null);
    setOwnOrders(null);
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
          setQuote(null);
          setOwnOrders(null);
        }
      }
      if (updates.book) setBook(updates.book);
      if (updates.ownOrders) setOwnOrders(updates.ownOrders);
      if (updates.ticker) setQuote(updates.ticker);
      if (updates.trades) setTape(updates.trades);
      if (updates.lag !== undefined) setExchangeAgeMs(updates.lag);
      if (updates.sequence !== undefined) setStreamMessages(updates.sequence);
      if (updates.diagnostics !== undefined) setFeedDiagnostics(updates.diagnostics);
      updates.book = undefined;
      updates.ownOrders = undefined;
      updates.ticker = undefined;
      updates.trades = undefined;
      updates.state = undefined;
      updates.lag = undefined;
      updates.sequence = undefined;
      updates.diagnostics = undefined;
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
        encodeURIComponent(marketInstrument) + '&depth=' + domDepth;
      socket = new WebSocket(url);
      socket.onopen = () => { retryMs = 400; };
      socket.onmessage = event => {
        if (!active) return;
        let payload: any;
        try { payload = JSON.parse(String(event.data)); }
        catch { socket?.close(); return; }
        if (payload.instrument !== marketInstrument) return;
        if ((payload.type === 'book' || payload.type === 'status') &&
            String(payload.depth) !== String(domDepth)) {
          socket?.close();
          return;
        }
        if (payload.type === 'status') {
          updates.state = String(payload.state);
          if (payload.diagnostics && typeof payload.diagnostics === 'object')
            updates.diagnostics = payload.diagnostics as FeedDiagnostics;
          // Drop older queued prices as soon as stream is not synced.
          if (payload.state !== 'synced') {
            updates.book = undefined;
            updates.ownOrders = undefined;
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
          if (payload.ownOrders && payload.ownOrders.instrument === marketInstrument)
            updates.ownOrders = payload.ownOrders as OwnOrders;
          updates.lag = typeof payload.exchangeAgeMs === 'number' ?
            payload.exchangeAgeMs : null;
          updates.sequence = Number(payload.messages) || 0;
        } else if (payload.type === 'own_orders' && payload.orders) {
          updates.ownOrders = payload.orders as OwnOrders;
        } else if (payload.type === 'ticker') {
          updates.ticker = payload as Quote;
        } else if (payload.type === 'trades') {
          updates.trades = payload as TradeTape;
        }
        queueRender();
      };
      socket.onclose = () => {
        if (!active) return;
        setClientWsDisconnects(n => n + 1);
        updates.state = 'disconnected';
        updates.book = undefined;
        updates.ownOrders = undefined;
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
  }, [key, status, marketInstrument, domDepth]);

  // One-shot read-only public clock check. A Deribit HTTPS request happens
  // ONLY after the operator presses the button inside collapsed diagnostics.
  // The raw book stream and its exchange-to-local value remain untouched.
  const probeDeribitClock = async () => {
    if (!key || !status || clockProbePending) return;
    const rawAtClick = streamState === 'synced' ? exchangeAgeMs : null;
    setClockProbePending(true);
    setClockProbeError('');
    setExchangeClock(null);
    try {
      const result = await api<ExchangeClockSample>(key, '/market/clock');
      if (result.status === 'ok' &&
          Number.isFinite(result.clockOffsetMs) &&
          Number.isFinite(result.rttMs) &&
          Number.isFinite(result.sampleUnixMs)) {
        setExchangeClock({ ...result, rawDeltaAtCheckMs: rawAtClick });
      } else {
        setClockProbeError('Некорректный ответ проверки часов Deribit.');
      }
    } catch {
      setClockProbeError('Не удалось получить время Deribit. Повторите проверку вручную.');
    } finally {
      setClockProbePending(false);
    }
  };

  // A fixed one-shot sample must never be mislabeled as a live latency
  // measurement. The clock estimate is paired with the RAW delta observed
  // when this operator explicitly started the diagnostic request.
  const adjustedDeltaMs = exchangeClock?.rawDeltaAtCheckMs == null
    ? null
    : exchangeClock.rawDeltaAtCheckMs - exchangeClock.clockOffsetMs;

  // REST account state is independent from RAW market data. Each interval
  // is an atomic batch: rejected/malformed sections are UNAVAILABLE, never
  // represented as zero open orders or zero positions. No overlapping polls.
  const refresh = useCallback(async () => {
    if (!key || !status) return;
    const epoch = privateReadGate.current.begin();
    if (epoch === null) return;
    const controller = new AbortController();
    pendingPrivateAbort.current = controller;
    setRefreshing(true);
    setAccountSnapshot(emptyReadSnapshot<Account>());
    setPositionsSnapshot(emptyReadSnapshot<Position>());
    setOrdersSnapshot(emptyReadSnapshot<Order>());
    try {
      if (!status.credentialsConfigured) {
        if (privateReadGate.current.accepts(epoch)) {
          setAccountSnapshot(unavailableReadSnapshot<Account>());
          setPositionsSnapshot(unavailableReadSnapshot<Position>());
          setOrdersSnapshot(unavailableReadSnapshot<Order>());
          setPrivateReadError('');
        }
        return;
      }

      const accountResults = await Promise.allSettled([
        api<Account>(key, '/account?currency=BTC', { signal: controller.signal }),
        api<Account>(key, '/account?currency=ETH', { signal: controller.signal }),
        api<Position[]>(key, '/positions', { signal: controller.signal }),
        api<Order[]>(key, '/orders', { signal: controller.signal })
      ]);
      // Aborted or re-keyed requests cannot replace a newer session.
      if (!privateReadGate.current.accepts(epoch)) return;
      const [btc, eth, pos, open] = accountResults;
      const sampledAt = Date.now();
      const accountNext = settleAccountPair<Account>(btc, eth, sampledAt, isAccount);
      const positionsNext = settleReadSnapshot<Position>(pos, sampledAt, isPosition);
      const ordersNext = settleReadSnapshot<Order>(open, sampledAt, isOrder);
      setAccountSnapshot(accountNext);
      setPositionsSnapshot(positionsNext);
      setOrdersSnapshot(ordersNext);
      setPrivateReadError(
        [accountNext, positionsNext, ordersNext].some(x => x.state !== 'ready')
          ? 'Часть данных счёта Deribit сейчас недоступна. Неполные снимки скрыты.'
          : ''
      );
    } catch {
      if (privateReadGate.current.accepts(epoch)) {
        setAccountSnapshot(unavailableReadSnapshot<Account>());
        setPositionsSnapshot(unavailableReadSnapshot<Position>());
        setOrdersSnapshot(unavailableReadSnapshot<Order>());
        setPrivateReadError('Не удалось обновить приватное состояние Deribit.');
      }
    } finally {
      if (privateReadGate.current.accepts(epoch)) {
        privateReadGate.current.finish(epoch);
        pendingPrivateAbort.current = null;
        setRefreshing(false);
      }
    }
  }, [key, status]);

  useEffect(() => {
    privateReadGate.current.invalidate();
    pendingPrivateAbort.current?.abort();
    pendingPrivateAbort.current = null;
    setAccountSnapshot(emptyReadSnapshot<Account>());
    setPositionsSnapshot(emptyReadSnapshot<Position>());
    setOrdersSnapshot(emptyReadSnapshot<Order>());
    setPrivateReadError('');
    if (!key || !status) return;
    void refresh();
    const interval = window.setInterval(() => { void refresh(); }, 8000);
    return () => {
      window.clearInterval(interval);
      // Re-key / unmount: abort in-flight private reads and forbid late commits.
      privateReadGate.current.invalidate();
      pendingPrivateAbort.current?.abort();
      pendingPrivateAbort.current = null;
    };
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
            <div className="stat"><span>Открытые заявки</span><strong>{status.credentialsConfigured ? snapshotCount(ordersSnapshot) ?? '—' : '—'}</strong><small>На бирже</small></div>
            <div className="stat"><span>Открытые позиции</span><strong>{status.credentialsConfigured ? snapshotCount(positionsSnapshot) ?? '—' : '—'}</strong><small>Все типы инструментов</small></div>
          </div>

          {error && <div className="notice danger"><ShieldAlert size={18}/> {error}</div>}
          {privateReadError && <div className="notice danger"><ShieldAlert size={18}/> {privateReadError}</div>}
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
                <div><span className="eyebrow">LIVE DERIBIT WEBSOCKET / EVENT-DRIVEN</span><h2>Стакан заявок</h2></div>
                <div className="stream-status" aria-live="polite">
                  <strong className={streamState === 'synced' ? 'success-text' : 'red-text'}>
                    {streamState === 'synced' ? (domDepth === 'raw' ? 'LIVE RAW' : 'LIVE 100MS') : streamState.toUpperCase()}
                  </strong>
                  <span>{marketInstrument}</span>
                </div>
              </div>
              <div className="dom-depth-toolbar">
                <label htmlFor="dom-depth">Depth</label>
                <select id="dom-depth" aria-label="Видимых уровней на сторону"
                  value={domDepth}
                  onChange={e => setDomDepth(e.target.value === 'raw'
                    ? 'raw' : Number(e.target.value) as DomDepth)}>
                  <option value={1}>1</option>
                  <option value={10}>10</option>
                  <option value={20}>20</option>
                  <option value="raw">RAW</option>
                </select>
                <span className="dom-depth-source"
                  title={domDepth === 'raw'
                    ? 'Полный поток RAW; в интерфейсе отображаются первые 50 уровней на сторону'
                    : 'Подписка Deribit ровно на ' + domDepth + ' уровней, снимки каждые 100 мс'}>
                  {domDepth === 'raw' ? 'RAW L2' : 'L2 ' + domDepth + ' · 100MS'}
                </span>
                <span className="dom-stream-lag" title="Разность времени Deribit и локальных часов Windows; зависит от синхронизации часов">
                  Δ exchange → local: {streamState === 'synced' && exchangeAgeMs !== null
                    ? exchangeAgeMs + ' ms' : '—'}
                </span>
              </div>
              <div className="dom-own-status" aria-live="polite">
                <span>Мои лимитные заявки:</span>
                <strong className={ownOrders?.state === 'ready' && streamState === 'synced'
                  ? 'success-text' : 'muted'}>
                  {ownOrders?.state === 'ready' && streamState === 'synced'
                    ? ownOrders.openOrders
                    : ownOrders?.state === 'unavailable' ? 'нет доступа'
                    : 'синхронизация'}
                </strong>
                {ownOrders?.state === 'ready' && <span>● на уровне — мой объём (наведи курсор)</span>}
              </div>
              {outsideOwnOrders.length > 0 && (
                <details className="dom-own-outside">
                  <summary>
                    Мои заявки вне {domDepth === 'raw' ? 'видимых 50' : domDepth} уровней: {outsideOwnOrders.length} ценовых уровней
                  </summary>
                  <div className="dom-own-outside-list">
                    {outsideOwnOrders.map(level => (
                      <div className="dom-own-outside-row" key={level.direction + ':' + level.price}>
                        <span className={level.direction === 'buy' ? 'success-text' : 'red-text'}>
                          {level.direction === 'buy' ? 'BUY' : 'SELL'}
                        </span>
                        <strong>{formatDomPrice(level.price, priceDecimals)}</strong>
                        <span>Мой объём: {num(level.amount, 6)}</span>
                        <span>Заявок: {level.orders}</span>
                      </div>
                    ))}
                  </div>
                  <p>Собственные заявки вне текущего диапазона.
                    Объём рынка по этим ценам здесь не предполагается.</p>
                </details>
              )}
              <div className="dom-ladder" ref={ladderScrollRef}
                aria-label={'Вертикальный стакан ' + marketInstrument}>
                <div className="dom-head">
                  <span>Цена</span><span>Объём</span><span>Совокупный</span>
                </div>
                <div className="dom-half dom-asks" aria-label="Asks — продавцы сверху">
                  {dom.asks.map(level => (
                    <div className={'dom-row ask' + (level.best ? ' best' : '') +
                      (ownLevels.has('sell:' + level.price) ? ' own-order-level' : '')}
                      key={'ask-' + level.price}
                      title={ownLevels.has('sell:' + level.price)
                        ? 'Мои Sell: ' + num(ownLevels.get('sell:' + level.price)?.amount, 6) +
                          ', ордеров: ' + ownLevels.get('sell:' + level.price)?.orders
                        : undefined}
                      aria-label={'Ask ' + level.price + ', объём ' + level.amount +
                        ', накоплено ' + level.cumulative +
                        (ownLevels.has('sell:' + level.price)
                          ? ', мой объём ' + ownLevels.get('sell:' + level.price)?.amount : '')}>
                      <span className="dom-bar ask-bar" style={{ width: level.percent + '%' }} aria-hidden="true" />
                      <strong className="dom-price">
                        {ownLevels.has('sell:' + level.price) &&
                          <span className="dom-own-marker" aria-label="Мой SELL ордер">●</span>}
                        {formatDomPrice(level.price, priceDecimals)}
                      </strong>
                      <span className="dom-size">{num(level.amount, 6)}</span>
                      <span className="dom-cumulative">{num(level.cumulative, 6)}</span>
                    </div>
                  ))}
                  {dom.asks.length === 0 && <p className="dom-empty">Ожидаем синхронизацию Ask</p>}
                </div>
                <div ref={ladderCenterRef} className="dom-reference-prices"
                  aria-label="Последняя сделка, маркировочная и индексная цены Deribit">
                  <div className="dom-reference-value">
                    <span>Last Trade Price</span>
                    <strong>{formatDomPrice(
                      streamState === 'synced'
                        ? (quote?.instrument === marketInstrument ? quote.lastPrice : null) ??
                          (tape?.instrument === marketInstrument
                            ? [...tape.trades].sort((a, b) =>
                                (b.timestamp ?? 0) - (a.timestamp ?? 0))[0]?.price
                            : null)
                        : null,
                      priceDecimals
                    )}</strong>
                  </div>
                  <div className="dom-reference-value">
                    <span>Mark Price</span>
                    <strong>{formatDomPrice(
                      streamState === 'synced' && quote?.instrument === marketInstrument
                        ? quote.markPrice : null,
                      priceDecimals
                    )}</strong>
                  </div>
                  <div className="dom-reference-value">
                    <span>Index Price</span>
                    <strong>{formatDomPrice(
                      streamState === 'synced' && quote?.instrument === marketInstrument
                        ? quote.indexPrice : null,
                      priceDecimals
                    )}</strong>
                  </div>
                </div>
                <div className="dom-half dom-bids" aria-label="Bids — покупатели снизу">
                  {dom.bids.map(level => (
                    <div className={'dom-row bid' + (level.best ? ' best' : '') +
                      (ownLevels.has('buy:' + level.price) ? ' own-order-level' : '')}
                      key={'bid-' + level.price}
                      title={ownLevels.has('buy:' + level.price)
                        ? 'Мои Buy: ' + num(ownLevels.get('buy:' + level.price)?.amount, 6) +
                          ', ордеров: ' + ownLevels.get('buy:' + level.price)?.orders
                        : undefined}
                      aria-label={'Bid ' + level.price + ', объём ' + level.amount +
                        ', накоплено ' + level.cumulative +
                        (ownLevels.has('buy:' + level.price)
                          ? ', мой объём ' + ownLevels.get('buy:' + level.price)?.amount : '')}>
                      <span className="dom-bar bid-bar" style={{ width: level.percent + '%' }} aria-hidden="true" />
                      <strong className="dom-price">
                        {ownLevels.has('buy:' + level.price) &&
                          <span className="dom-own-marker" aria-label="Мой BUY ордер">●</span>}
                        {formatDomPrice(level.price, priceDecimals)}
                      </strong>
                      <span className="dom-size">{num(level.amount, 6)}</span>
                      <span className="dom-cumulative">{num(level.cumulative, 6)}</span>
                    </div>
                  ))}
                  {dom.bids.length === 0 && <p className="dom-empty">Ожидаем синхронизацию Bid</p>}
                </div>
              </div>
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
                <span className="count" aria-live="polite">{positionsSnapshot.state === 'ready'
                  ? positions.length + ' positions · ' + new Date(positionsSnapshot.sampledAtUnixMs!).toISOString().slice(11, 19) + ' UTC'
                  : positionsSnapshot.state === 'loading' ? 'Загрузка позиций…' : 'Позиции недоступны'}</span>
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
                <tbody>{positionsSnapshot.state !== 'ready'
                  ? <tr><td colSpan={6} className="empty-cell">Данные позиций не подтверждены биржей.</td></tr>
                  : positions.length ? positions.map((p, i) => <tr key={p.instrument + i}>
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
            <div className="panel-header"><div><span className="eyebrow">ORDER MANAGEMENT</span><h2>Активные ордера</h2></div><span className="count" aria-live="polite">{ordersSnapshot.state === 'ready'
                ? orders.length + ' orders · ' + new Date(ordersSnapshot.sampledAtUnixMs!).toISOString().slice(11, 19) + ' UTC'
                : ordersSnapshot.state === 'loading' ? 'Загрузка ордеров…' : 'Ордера недоступны'}</span></div>
            <div className="table-wrap"><table><thead><tr><th>Инструмент</th><th>Order ID</th><th>Сторона</th><th className="right">Объём</th><th className="right">Цена</th><th>Состояние</th><th></th></tr></thead>
              <tbody>{ordersSnapshot.state !== 'ready'
                ? <tr><td colSpan={7} className="empty-cell">Данные ордеров не подтверждены биржей.</td></tr>
                : orders.length ? orders.map((o, i) => <tr key={o.orderId || i}>
                <td className="mono strong">{o.instrument}</td>
                <td className="mono muted truncate-cell" title={o.orderId || ''}>{o.orderId || '—'}</td>
                <td className={o.side === 'buy' ? 'success-text' : 'red-text'}>{o.side}</td>
                <td className="right mono">{num(o.amount, 6)}</td><td className="right mono">{num(o.price)}</td>
                <td><span className="state-pill">{o.state || '—'}</span></td>
                <td className="right"><button className="cancel" disabled={!status.tradingEnabled || loading || !o.orderId}
                  onClick={() => void cancelOrder(o.orderId)}>Cancel</button></td>
              </tr>) : <tr><td colSpan={7} className="empty-cell">Открытых ордеров нет или данные недоступны.</td></tr>}</tbody></table></div>
          </section>

          <details className="terminal-diagnostics">
            <summary>Диагностика рыночного потока</summary>
            <div className="terminal-diagnostics-content">
              <span>Инструмент: {marketInstrument}</span>
              <span>Статус: {streamState}</span>
              <span>Public L2: {feedDiagnostics?.publicL2State ?? streamState}</span>
              <span>Выбранная глубина: {domDepth}</span>
              <span>Подписка Deribit: {feedDiagnostics?.exchangeChannel ?? '—'}</span>
              <span>Режим биржевого потока: {feedDiagnostics?.exchangeMode ?? '—'}</span>
              <span>Private overlay: {ownOrders?.state ?? feedDiagnostics?.privateOrdersState ?? '—'}</span>
              <span>Последний код сбоя: {feedDiagnostics?.reasonCode ?? '—'}</span>
              <span>Последний сбой UTC: {feedDiagnostics?.failureAtUtc ?? '—'}</span>
              <span>Expected change_id: {feedDiagnostics?.expectedChangeId ?? '—'}</span>
              <span>Incoming prev_change_id: {feedDiagnostics?.prevChangeId ?? '—'}</span>
              <span>Incoming change_id: {feedDiagnostics?.changeId ?? '—'}</span>
              <span>Reconnect attempts: {feedDiagnostics?.reconnectAttempts ?? 0}</span>
              <span>CLIENT_WS_DISCONNECT: {clientWsDisconnects}</span>
              <span>Счётчики ошибок: {feedDiagnostics?.counters
                ? Object.entries(feedDiagnostics.counters).map(([code, count]) =>
                    code + '=' + count).join(' · ') || '—' : '—'}</span>
              <span>Change ID: {book?.changeId ?? '—'}</span>
              <span>Сообщений L2: {streamMessages}</span>
              <span>Уровней на сторону: {domDepth === 'raw' ? 'RAW (показаны до 50)' : domDepth}</span>
              <span>Моих цен вне видимого диапазона: {outsideOwnOrders.length}</span>
              <span>Приватные ордера: {ownOrders?.state ?? 'нет данных'}</span>
              <span>Открытых собственных ордеров: {ownOrders?.state === 'ready' ? ownOrders.openOrders : '—'}</span>
              <span>Δ exchange → local (текущая): {exchangeAgeMs === null ? '—' : exchangeAgeMs + ' ms'}</span>
              <button type="button" className="refresh"
                onClick={() => { void probeDeribitClock(); }}
                disabled={clockProbePending || !status}>
                {clockProbePending ? 'Проверка часов…' : 'Проверить часы Deribit'}
              </button>
              {clockProbeError && <span className="error">{clockProbeError}</span>}
              <span>Windows − Deribit (последний ручной замер): {exchangeClock === null
                ? '—' : exchangeClock.clockOffsetMs + ' ms'}</span>
              <span>RTT public/get_time: {exchangeClock === null ? '—' : exchangeClock.rttMs + ' ms'}</span>
              <span>Δ при запуске проверки: {exchangeClock?.rawDeltaAtCheckMs == null
                ? '—' : exchangeClock.rawDeltaAtCheckMs + ' ms'}</span>
              <span>Δ без смещения часов (оценка на момент проверки):
                {' '}{adjustedDeltaMs === null ? '—' : adjustedDeltaMs + ' ms'}</span>
              <span>Время ручного замера UTC: {exchangeClock === null
                ? '—' : new Date(exchangeClock.sampleUnixMs).toISOString()}</span>
              <span>Ценовой шаг: {contractSpec?.name === marketInstrument ? contractSpec.tickSize : '—'}</span>
              <span>Дополнительных правил шага: {contractSpec?.tickSizeSteps.length ?? '—'}</span>
              <span>Знаков цены по спецификации: {priceDecimals ?? '—'}</span>
              {contractSpecError && <span className="error">{contractSpecError}</span>}
              <p>RAW: полный снимок и контроль непрерывности изменений; 1/10/20: полные группированные снимки каждые 100 мс.
                Δ рассчитывается по времени биржи и локальным часам Windows.
                public/get_time вызывается только по нажатию кнопки.
                Значения ручной проверки остаются снимком момента запроса,
                не обновляются автоматически. Поправка по середине RTT
                предполагает приблизительно симметричный сетевой путь;
                это не точная односторонняя задержка.
                Объёмы показаны в исходных единицах Deribit.</p>
            </div>
          </details>

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
