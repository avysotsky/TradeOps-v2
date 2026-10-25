import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
const ui = readFileSync(new URL('../src/Terminal.tsx', import.meta.url), 'utf8');
const nav = readFileSync(new URL('../src/VanWorkbench.tsx', import.meta.url), 'utf8');
const css = readFileSync(new URL('../src/van-parity.css', import.meta.url), 'utf8');
const main = readFileSync(new URL('../src/main.tsx', import.meta.url), 'utf8');
test('VAN three-column desktop and single-column mobile layout', () => {
 assert.match(ui, /<div className="van-workstation">/);
 assert.match(ui, /<VanNavigation instrument=\{marketInstrument\}/);
 assert.match(ui, /<VanTradeChart instrument=\{marketInstrument\}/);
 assert.match(css, /grid-template-areas:"market book ticket" "tape book ticket"/);
 assert.match(css, /grid-template-areas:"market" "book" "ticket" "tape"/);
 assert.match(main, /import '\.\/van-parity\.css';/);
});
test('VAN positions/orders dock has controlled tabs and disabled absent APIs', () => {
 assert.match(ui, /<VanBottomTabs selected=\{accountTab\} onSelect=\{setAccountTab\}/);
 assert.match(ui, /hidden=\{accountTab !== 'positions'\}/);
 assert.match(ui, /hidden=\{accountTab !== 'orders'\}/);
 assert.match(nav, /'Order history', 'Trade history', 'Position history', 'Funding', 'Exercise', 'Assets'/);
 assert.match(nav, /title="Нет подтверждённого API-контракта; данные не имитируются"/);
 assert.match(nav, /tab.count \?\? '—'/);
});
test('tick chart consumes only real existing public WS tape and clearly avoids synthetic candles', () => {
 assert.match(ui, /tape\?\.instrument === marketInstrument \? tape\.trades : \[\]/);
 assert.match(nav, /t.price !== null && Number.isFinite\(t.price\)/);
 assert.match(nav, /t.timestamp !== null && Number.isFinite\(t.timestamp\)/);
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
