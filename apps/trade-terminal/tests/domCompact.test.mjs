import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const ui = readFileSync(new URL('../src/Terminal.tsx', import.meta.url), 'utf8');
const css = readFileSync(new URL('../src/terminal.css', import.meta.url), 'utf8');

test('vertical DOM column order: PRICE, AMOUNT, CUMULATIVE', () => {
  assert.match(ui, /<span>Цена<\/span><span>Объём<\/span><span>Совокупный<\/span>/);
  const asks = ui.match(/<div className="dom-half dom-asks"[\s\S]*?<div className="dom-mid"/)?.[0];
  const bids = ui.match(/<div className="dom-half dom-bids"[\s\S]*?<\/div>\s*<\/div>\s*<div className="dom-legend"/)?.[0];
  assert.ok(asks, 'ASK block present');
  assert.ok(bids, 'BID block present');
  for (const block of [asks, bids]) {
    const price = block.indexOf('className="dom-price"');
    const amount = block.indexOf('className="dom-size"');
    const cumulative = block.indexOf('className="dom-cumulative"');
    assert.ok(price >= 0 && price < amount && amount < cumulative,
      'Each quote row must order price, amount and cumulative amount');
  }
});

test('compact DOM uses narrow fixed columns and <=24px depth rows', () => {
  const compact = css.slice(css.indexOf('/* TO-V2-43: compact Deribit-style vertical DOM.'));
  assert.ok(compact.length > 1000);
  assert.match(compact, /grid-template-columns: 105px 88px 102px;/);
  assert.match(compact, /column-gap: 4px;/);
  assert.match(compact, /min-height: 23px;/);
  assert.match(compact, /min-height: 24px;/);
  assert.match(compact, /min-height: 30px;/);
  assert.match(compact, /grid-template-columns: 92px 80px 94px;/);
  assert.match(compact, /overflow-x: auto;/);
});

test('no new market polling, DOM button-to-order or trading side effects', () => {
  assert.match(ui, /new WebSocket\(url\)/);
  assert.doesNotMatch(ui, /\/market\/orderbook\?instrument=/);
  assert.doesNotMatch(ui, /\/market\/trades\?instrument=/);
  const ladder = ui.slice(ui.indexOf('<div className="dom-ladder"'),
    ui.indexOf('<div className="dom-legend"'));
  assert.doesNotMatch(ladder, /onClick|onMouseDown|orders\/preview|orders\/confirm/);
});
