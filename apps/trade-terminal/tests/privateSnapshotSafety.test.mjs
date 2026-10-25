import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import {
  emptyReadSnapshot, settleReadSnapshot, settleAccountPair,
  snapshotCount, PrivateReadGate
} from '../src/privateReadState.ts';

const validOrder = x => x !== null && typeof x === 'object' &&
  typeof x.orderId === 'string';

test('empty exchange orders are distinguishable from unavailable private API', () => {
  const ready = settleReadSnapshot({ status: 'fulfilled', value: [] }, 1000, validOrder);
  const failed = settleReadSnapshot({ status: 'rejected', reason: new Error('private') }, 1001, validOrder);
  const malformed = settleReadSnapshot({ status: 'fulfilled', value: {} }, 1002, validOrder);
  const poisoned = settleReadSnapshot({ status: 'fulfilled', value: [null] }, 1003, validOrder);
  assert.equal(ready.state, 'ready');
  assert.equal(snapshotCount(ready), 0);
  assert.equal(ready.sampledAtUnixMs, 1000);
  for (const snapshot of [failed, malformed, poisoned, emptyReadSnapshot()]) {
    assert.equal(snapshotCount(snapshot), null);
    assert.deepEqual(snapshot.items, []);
  }
  assert.equal(failed.state, 'unavailable');
  assert.equal(malformed.state, 'unavailable');
  assert.equal(poisoned.state, 'unavailable');
});

test('BTC and ETH account states commit atomically or not at all', () => {
  const isAccount = x => x !== null && typeof x === 'object' &&
    (x.currency === 'BTC' || x.currency === 'ETH');
  const btc = { status: 'fulfilled', value: { currency: 'BTC', equity: 10 } };
  const eth = { status: 'fulfilled', value: { currency: 'ETH', equity: 20 } };
  const ok = settleAccountPair(btc, eth, 42, isAccount);
  assert.deepEqual(ok.items.map(x => x.currency), ['BTC', 'ETH']);
  assert.equal(ok.state, 'ready');
  const missing = settleAccountPair(btc, { status: 'rejected', reason: 502 }, 43, isAccount);
  assert.equal(missing.state, 'unavailable');
  assert.deepEqual(missing.items, []);
  const corrupted = settleAccountPair(btc, { status: 'fulfilled', value: 'invalid' }, 44, isAccount);
  assert.equal(corrupted.state, 'unavailable');
});

test('request epoch prevents overlapped polls and rejects late responses after cleanup', () => {
  const gate = new PrivateReadGate();
  const old = gate.begin();
  assert.notEqual(old, null);
  assert.equal(gate.begin(), null, 'poll is already in flight');
  assert.equal(gate.accepts(old), true);
  gate.invalidate(); // React effect cleanup / session re-key
  assert.equal(gate.accepts(old), false);
  const current = gate.begin();
  assert.notEqual(current, null);
  assert.notEqual(current, old);
  gate.finish(old); // late completion must not unlock current poll
  assert.equal(gate.begin(), null);
  assert.equal(gate.accepts(current), true);
  gate.finish(current);
  assert.equal(gate.accepts(current), false);
  assert.notEqual(gate.begin(), null);
});

test('React private tables fail closed; exchange execution remains disabled', () => {
  const ui = readFileSync(new URL('../src/Terminal.tsx', import.meta.url), 'utf8');
  const launcher = readFileSync(new URL('../../../scripts/deribit/Start-MainnetReactTerminalReadOnly.ps1', import.meta.url), 'utf8');
  assert.match(ui, /privateReadGate\.current\.accepts\(epoch\)/);
  assert.match(ui, /privateReadGate\.current\.invalidate\(\)/);
  assert.match(ui, /pendingPrivateAbort\.current\?\.abort\(\)/);
  assert.match(ui, /settleReadSnapshot<Order>/);
  assert.match(ui, /settleReadSnapshot<Position>/);
  assert.match(ui, /settleAccountPair<Account>/);
  assert.match(ui, /ordersSnapshot\.state !== 'ready'/);
  assert.match(ui, /positionsSnapshot\.state !== 'ready'/);
  assert.match(launcher, /DeribitTerminal__ExecutionEnabled = 'false'/);
  assert.match(launcher, /DeribitTerminal__MainnetExecutionEnabled = 'false'/);
});
