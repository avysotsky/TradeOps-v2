import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const ui = readFileSync(new URL('../src/Terminal.tsx', import.meta.url), 'utf8');
const css = readFileSync(new URL('../src/terminal.css', import.meta.url), 'utf8');
const ladder = ui.slice(ui.indexOf('<div className="dom-ladder"'),
  ui.indexOf('<section className="panel market-feed">',
    ui.indexOf('<div className="dom-ladder"') + 1));

test('vertical DOM keeps PRICE, AMOUNT, CUMULATIVE and dense layout', () => {
  assert.match(ui, /<span>Цена<\/span><span>Объём<\/span><span>Совокупный<\/span>/);
  const asks = ladder.slice(ladder.indexOf('className="dom-half dom-asks"'),
    ladder.indexOf('className="dom-reference-prices"'));
  const bids = ladder.slice(ladder.indexOf('className="dom-half dom-bids"'));
  assert.ok(asks.startsWith('className="dom-half dom-asks"'));
  assert.ok(bids.startsWith('className="dom-half dom-bids"'));
  for (const block of [asks, bids]) {
    const price = block.indexOf('className="dom-price"');
    const volume = block.indexOf('className="dom-size"');
    const cumulative = block.indexOf('className="dom-cumulative"');
    assert.ok(price >= 0 && price < volume && volume < cumulative);
  }
  const compact = css.slice(css.indexOf('/* TO-V2-44: ultra-compact Deribit-style vertical DOM.'));
  assert.match(compact, /grid-template-columns: 105px 88px 102px;/);
  assert.match(compact, /column-gap: 4px;/);
  assert.match(compact, /min-height: 18px;/);
  assert.match(compact, /min-height: 19px;/);
  assert.match(compact, /min-height: 20px;/);
});

test('center strip displays LAST TRADE, MARK, INDEX in exactly this order', () => {
  const from = ladder.indexOf('className="dom-reference-prices"');
  const to = ladder.indexOf('className="dom-half dom-bids"', from);
  const center = ladder.slice(from, to);
  assert.ok(from > 0 && to > from);
  const last = center.indexOf('Last Trade Price');
  const mark = center.indexOf('Mark Price');
  const index = center.indexOf('Index Price');
  assert.ok(last > 0 && last < mark && mark < index);
  assert.doesNotMatch(center, /dom\.spread\.(mid|absolute)|SPREAD|dom-mid-price/);
  const formats = center.match(/formatDomPrice\(/g) ?? [];
  assert.equal(formats.length, 3, 'All three reference prices use selected contract tick precision');
  assert.match(css, /\.dom-reference-prices\s*\{/);
});

test('no legend or raw transport explanation directly underneath the DOM', () => {
  assert.doesNotMatch(ladder, /dom-legend|Raw L2 snapshot \+ каждое изменение|Последний change_id/);
  const info = ui.slice(ui.indexOf('<details className="terminal-diagnostics"'));
  assert.match(info, /<summary>Диагностика рыночного потока<\/summary>/);
  assert.match(info, /Change ID:/);
  assert.match(info, /Сообщений L2:/);
  assert.match(info, /Δ exchange → local \(текущая\):/);
  assert.match(info, /Ценовой шаг:/);
});

test('all book and reference price cells use contract metadata, no half tick', () => {
  const prices = ladder.match(/formatDomPrice\(/g) ?? [];
  assert.equal(prices.length, 5, 'Ask, Bid, Last, Mark and Index use same spec-decimal function');
  assert.match(ui, /contractPriceDecimals\(/);
  assert.match(ui, /\/market\/instrument\?instrument=/);
  assert.doesNotMatch(ui, /domPriceDecimals\(|marketKind === 'option' \? 8 : 2\)\}/);
  assert.doesNotMatch(ladder, /dom\.spread\.mid|dom\.spread\.absolute/);
});

test('read-only event-driven market stream remains isolated from order ticket', () => {
  assert.match(ui, /new WebSocket\(url\)/);
  assert.doesNotMatch(ui, /\/market\/orderbook\?instrument=/);
  assert.doesNotMatch(ui, /\/market\/trades\?instrument=/);
  assert.doesNotMatch(ladder, /onClick|onMouseDown|orders\/preview|orders\/confirm/);
});

test('Depth selector offers native Deribit 1/10/20 and explicit RAW', () => {
  assert.match(ui, /const \[domDepth, setDomDepth\] = useState<DomDepth>\('raw'\)/);
  assert.match(ui, /<option value=\{10\}>10<\/option>/);
  assert.match(ui, /<option value=\{1\}>1<\/option>/);
  assert.match(ui, /<option value=\{20\}>20<\/option>/);
  assert.match(ui, /<option value="raw">RAW<\/option>/);
  assert.match(ui, /toPriceLadder\([\s\S]*?book : null, domDepth\)/);
  assert.match(ui, /ownOrdersOutsideDepth\(ownOrders.levels, dom\)/);
  assert.match(ui, /Мои заявки вне \{domDepth === 'raw' \? 'видимых 50' : domDepth\} уровней/);
  assert.match(ui, /formatDomPrice\(level.price, priceDecimals\)/);
  assert.match(css, /\.dom-ladder\s*\{\s*position: relative;\s*max-height: 460px;/);
  assert.match(ui, /ladderScrollRef/);
  assert.match(ui, /ladderCenterRef/);
  assert.doesNotMatch(ui, /\/market\/orderbook\?instrument=/);
  assert.match(ui, /new WebSocket\(url\)/);
});

test('Depth controls show live exchange-to-local lag on far right in same row', () => {
  const marker = ui.indexOf('<div className="dom-depth-toolbar">');
  assert.ok(marker >= 0);
  const block = ui.slice(marker, ui.indexOf('<div className="dom-own-status"', marker));
  const depth = block.indexOf('<label htmlFor="dom-depth">Depth</label>');
  const options = block.indexOf('<option value="raw">RAW</option>');
  const raw = block.indexOf('className="dom-depth-source"');
  const lag = block.indexOf('className="dom-stream-lag"');
  assert.ok(depth >= 0 && depth < options && options < raw && raw < lag);
  assert.match(block, /Δ exchange → local:/);
  assert.match(block, /streamState === 'synced' && exchangeAgeMs !== null/);
  assert.match(block, /exchangeAgeMs \+ ' ms' : '—'/);
  assert.doesNotMatch(block, /Глубина \/ сторона/);
  assert.match(css, /\.dom-depth-toolbar > \.dom-stream-lag\s*\{\s*margin-left: auto;/);
  assert.match(css, /font-variant-numeric: tabular-nums;/);
});

test('positions and orders tables are dense but preserve adjustable readable fonts', () => {
  const compact = css.slice(css.indexOf('/* TO-V2-48: Depth'));
  assert.ok(compact.length > 400);
  assert.match(compact, /\.data-panel th\s*\{\s*padding: 4px 8px;/);
  assert.match(compact, /\.data-panel td\s*\{\s*padding: 2px 8px;/);
  assert.match(compact, /\.data-panel \.state-pill\s*\{\s*padding: 2px 6px;/);
  assert.match(compact, /\.data-panel \.cancel\s*\{\s*padding: 2px 7px;/);
  assert.match(css, /\.data-panel td\s*\{\s*padding: 18px;\s*font-size: var\(--table-font-size, 16px\);/);
  assert.match(ui, /setTableFontSize\(size => Math.min\(20, size \+ 2\)\)/);
  assert.match(ui, /EXCHANGE STATE<\/span><h2>Открытые позиции<\/h2>/);
  assert.match(ui, /ORDER MANAGEMENT<\/span><h2>Активные ордера<\/h2>/);
});
