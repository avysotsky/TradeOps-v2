import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const ui = readFileSync(new URL('../src/Terminal.tsx', import.meta.url), 'utf8');
const hub = readFileSync(new URL('../../TradeOps.TerminalApi/Market/DeribitRawFeedHub.cs', import.meta.url), 'utf8');
const book = readFileSync(new URL('../../TradeOps.TerminalApi/Market/DeribitRawBook.cs', import.meta.url), 'utf8');
const launcher = readFileSync(new URL('../../../scripts/deribit/Start-MainnetReactTerminalReadOnly.ps1', import.meta.url), 'utf8');

test('L2 remains fail closed with explicit classified sequence diagnostics', () => {
  assert.match(book, /prev_change_id/);
  assert.match(book, /previousId != _lastChange/);
  assert.match(book, /id <= _lastChange/);
  assert.match(book, /L2_PREV_CHANGE_MISMATCH/);
  assert.match(book, /L2_MALFORMED/);
  assert.match(book, /L2_CROSSED/);
  assert.match(book, /Invalidate\(\)/);
});

test('connection failures are sanitized and private parser errors do not kill public L2', () => {
  for (const code of ['WS_TIMEOUT', 'WS_CLOSED', 'WS_AUTH_SUBSCRIPTION', 'WS_MALFORMED_FRAME', 'RECONNECT_ATTEMPT']) {
    assert.ok(hub.includes(code), 'missing ' + code);
  }
  assert.match(hub, /catch \(Exception\)[\s\S]*?_ownOrders\.Reset\("unavailable"\)/);
  assert.match(hub, /reasonCode = _lastFailureCode/);
  assert.match(hub, /expectedChangeId = _lastBookFailure\?\.ExpectedChangeId/);
  assert.doesNotMatch(hub, /ex\.ToString\(\)|ex\.Message/);
  assert.doesNotMatch(hub, /private\/(?:buy|sell|cancel)/);
});

test('collapsed operator diagnostics distinguish public/private and local websocket failures', () => {
  const panel = ui.slice(ui.indexOf('<details className="terminal-diagnostics"'));
  assert.match(panel, /Public L2:/);
  assert.match(panel, /Private overlay:/);
  assert.match(panel, /CLIENT_WS_DISCONNECT:/);
  assert.match(panel, /Expected change_id:/);
  assert.match(panel, /Incoming prev_change_id:/);
  assert.match(panel, /Reconnect attempts:/);
  assert.match(panel, /feedDiagnostics\?\.counters/);
  assert.match(ui, /setClientWsDisconnects\(n => n \+ 1\)/);
  assert.match(ui, /streamState === 'synced' && book\?\.instrument/);
});

test('pinned launcher still has both mainnet execution switches hard-disabled', () => {
  assert.match(launcher, /to-v2-52-private-snapshot-freshness/);
  assert.match(launcher, /DeribitTerminal__ExecutionEnabled = 'false'/);
  assert.match(launcher, /DeribitTerminal__MainnetExecutionEnabled = 'false'/);
  assert.match(launcher, /TRADEOPS_RAW_L2_REGRESSIONS_FAILED/);
});
