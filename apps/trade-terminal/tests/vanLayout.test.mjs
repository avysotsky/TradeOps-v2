import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createVanTickSeries } from '../src/vanChartSeries.ts';
const ui = readFileSync(new URL('../src/Terminal.tsx', import.meta.url), 'utf8');
const nav = readFileSync(new URL('../src/VanWorkbench.tsx', import.meta.url), 'utf8');
const css = readFileSync(new URL('../src/van-parity.css', import.meta.url), 'utf8');
const main = readFileSync(new URL('../src/main.tsx', import.meta.url), 'utf8');
test('VAN three-column desktop and single-column mobile layout', () => {
 assert.match(ui, /<div className="van-workstation">/);
 assert.match(ui, /<VanNavigation instrument=\{marketInstrument\} environment=\{status.environment\}/);
 assert.match(ui, /<VanTradeChart instrument=\{marketInstrument\}/);
 assert.match(css, /grid-template-areas:"market book ticket" "tape book ticket"/);
 assert.match(css, /grid-template-areas:"market" "book" "ticket" "tape"/);
 assert.match(main, /import '\.\/van-parity\.css';/);
});
test('VAN positions/orders dock has controlled tabs and disabled absent APIs', () => {
 assert.match(ui, /<VanBottomTabs selected=\{accountTab\} onSelect=\{setAccountTab\}/);
 assert.match(ui, /hidden=\{accountTab !== 'positions'\}/);
 assert.match(ui, /hidden=\{accountTab !== 'orders'\}/);
 assert.match(ui, /role="tabpanel" aria-labelledby="van-account-tab-positions"/);
 assert.match(ui, /role="tabpanel" aria-labelledby="van-account-tab-orders"/);
 assert.match(nav, /onKeyDown=\{event => onTabKeyDown\(event, tab.id\)\}/);
 assert.match(nav, /'ArrowLeft', 'ArrowRight', 'Home', 'End'/);
 assert.match(nav, /'Order history', 'Trade history', 'Position history', 'Funding', 'Exercise', 'Assets'/);
 assert.match(nav, /title="Нет подтверждённого API-контракта; данные не имитируются"/);
 assert.match(nav, /tab.count \?\? '—'/);
});
test('tick chart consumes only real existing public WS tape and clearly avoids synthetic candles', () => {
 assert.match(ui, /streamState === 'synced' && tape\?\.instrument === marketInstrument \? tape\.trades : \[\]/);
 assert.match(nav, /createVanTickSeries\(ticks, range\)/);
 assert.match(nav, /aria-pressed=\{range === option\}/);
 assert.match(nav, /исторические свечи пока недоступны/);
 assert.doesNotMatch(nav, /fetch\(|WebSocket\(|orders\/confirm|window\./);
});
test('RAW L2, live lag, private snapshot state and font controls remain in terminal', () => {
 assert.match(ui, /new WebSocket\(url\)/);
 assert.match(ui, /<option value="raw">RAW<\/option>/);
 assert.match(ui, /Δ exchange → local:/);
 assert.match(ui, /PrivateReadGate/);
 assert.match(ui, /setTableFontSize\(size => Math.min\(20, size \+ 2\)\)/);
 assert.match(css, /\.van-dock-pane\[hidden\]/);
});
test('mainnet order actions disabled in React in addition to backend fail-closed configuration', () => {
 assert.match(ui, /disabled=\{status.environment === 'mainnet' \|\| !status.tradingEnabled/);
 assert.match(ui, /disabled=\{loading \|\| preview.environment === 'mainnet'\}/);
 assert.match(nav, /MAINNET READ-ONLY/);
});

test('tick chart derives chronological coordinates from verified public trades', () => {
 const series = createVanTickSeries([
  { price: 104, timestamp: 5000 }, { price: 100, timestamp: 1000 },
  { price: 102, timestamp: 3000 }, { price: null, timestamp: 4000 },
  { price: NaN, timestamp: 4500 }, { price: 0, timestamp: 4900 }
 ], 'all');
 assert.deepEqual(series.points.map(p => p.price), [100, 102, 104]);
 assert.equal(series.low, 100);
 assert.equal(series.high, 104);
 assert.equal(series.latestTimestamp, 5000);
 assert.equal(series.polyline.split(' ').length, 3);
 assert.equal(series.polyline.split(' ')[0], '12.00,138.00');
 assert.equal(series.polyline.split(' ')[2], '468.00,22.00');
});
test('tick chart time windows use last OBSERVED trade and cap memory', () => {
 const t = [1000, 61_000, 120_000, 360_000, 1_000_000]
   .map((timestamp, i) => ({timestamp, price:100 + i}));
 assert.deepEqual(createVanTickSeries(t,'1m').points.map(p=>p.timestamp),[1_000_000]);
 assert.deepEqual(createVanTickSeries(t,'5m').points.map(p=>p.timestamp),[1_000_000]);
 assert.deepEqual(createVanTickSeries(t,'15m').points.map(p=>p.timestamp),[120_000,360_000,1_000_000]);
 assert.equal(createVanTickSeries(t,'all').points.length,5);
 const many = Array.from({length:180},(_,i)=>({price:100+i,timestamp:1000+i}));
 assert.equal(createVanTickSeries(many,'all').points.length,120);
});
test('zero valid ticks has no synthetic or NaN coordinates', () => {
 assert.deepEqual(createVanTickSeries([
  {price:null,timestamp:1}, {price:NaN,timestamp:1}, {price:100,timestamp:null}
 ],'all'), {points:[],low:null,high:null,latestTimestamp:null,polyline:''});
});
test('flat or simultaneous trades produce finite non-degenerate chart', () => {
 const series = createVanTickSeries([
  {price:10,timestamp:3000},{price:10,timestamp:3000},{price:10,timestamp:3000}
 ],'all');
 assert.equal(series.polyline,'12.00,80.00 240.00,80.00 468.00,80.00');
 assert.ok(!series.polyline.includes('NaN'));
});

test('VAN 1280px reference shell constrains desktop, preserving 312px RAW-safe book', () => {
 assert.match(css, /@media \(min-width: 1161px\)/);
 assert.match(css, /width: min\(100%, 1280px\)/);
 assert.match(css, /grid-template-columns: minmax\(420px,1fr\) 312px 300px;/);
 assert.match(css, /grid-template-areas: "market book ticket" "tape book ticket"/);
 assert.match(css, /\.van-workstation \.dom-ladder \{ max-width: 100%; \}/);
});
