import test from 'node:test';
import assert from 'node:assert/strict';
import { toPriceLadder } from '../src/marketDepth.ts';

test('asks descend toward spread, bids descend away from spread', () => {
  const input = {
    asks: [
      { price: 103, amount: 3 },
      { price: 101, amount: 1 },
      { price: 102, amount: 2 }
    ],
    bids: [
      { price: 98, amount: 1 },
      { price: 100, amount: 2 },
      { price: 99, amount: 5 }
    ]
  };
  const result = toPriceLadder(input);

  assert.deepEqual(result.asks.map(x => x.price), [103, 102, 101]);
  assert.deepEqual(result.bids.map(x => x.price), [100, 99, 98]);
  assert.equal(result.asks[2].best, true);
  assert.equal(result.bids[0].best, true);
  assert.deepEqual(result.asks.map(x => x.cumulative), [6, 3, 1]);
  assert.deepEqual(result.bids.map(x => x.cumulative), [2, 7, 8]);
  assert.equal(result.asks[0].percent, 100);
  assert.equal(result.bids[2].percent, 100);
  assert.equal(result.spread?.absolute, 1);
  assert.equal(result.spread?.mid, 100.5);
  assert.ok(Math.abs(result.spread.bps - 99.5024875622) < 1e-8);
  assert.deepEqual(input.asks.map(x => x.price), [103, 101, 102],
    'Never mutate the received market snapshot.');
});

test('No stale prices while raw feed is disconnected or invalid', () => {
  const result = toPriceLadder(null);
  assert.deepEqual(result, { asks: [], bids: [], spread: null });
  const crossed = toPriceLadder({
    asks: [{ price: 99, amount: 4 }],
    bids: [{ price: 100, amount: 2 }]
  });
  assert.equal(crossed.spread, null);
});

test('Prices remain native, including small ETH option prices', () => {
  const ladder = toPriceLadder({
    asks: [{ price: 0.0015, amount: 10 }, { price: 0.001, amount: 8 }],
    bids: [{ price: 0.0008, amount: 5 }, { price: 0.0005, amount: 7 }]
  });
  assert.equal(ladder.asks[1].price, 0.001);
  assert.equal(ladder.bids[0].price, 0.0008);
  assert.ok(Math.abs(ladder.spread.absolute - 0.0002) < 1e-12);
  assert.ok(Math.abs(ladder.spread.mid - 0.0009) < 1e-12);
});

test('Max 10 levels per side and invalid rows excluded', () => {
  const data = Array.from({ length: 20 }, (_, i) =>
    ({ price: 200 - i, amount: 1 }));
  const out = toPriceLadder({
    asks: [
      { price: Number.NaN, amount: 1 },
      ...data.map(x => ({ price: 201 + (200 - x.price), amount: x.amount }))
    ],
    bids: data
  });
  assert.equal(out.asks.length, 10);
  assert.equal(out.bids.length, 10);
  assert.equal(out.asks[9].best, true);
  assert.equal(out.bids[0].best, true);
});
