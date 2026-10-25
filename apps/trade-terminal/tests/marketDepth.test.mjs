import test from 'node:test';
import assert from 'node:assert/strict';
import { toPriceLadder, ownOrdersOutsideDepth, contractPriceDecimals, formatDomPrice } from '../src/marketDepth.ts';

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
  }, 10);
  assert.equal(out.asks.length, 10);
  assert.equal(out.bids.length, 10);
  assert.equal(out.asks[9].best, true);
  assert.equal(out.bids[0].best, true);
});


test('contract tick_size fixes one price decimal for all ask/bid rows', () => {
  const contract = {
    name: 'ETH-PERPETUAL',
    kind: 'future',
    tickSize: 0.5,
    tickSizeSteps: []
  };
  const decimals = contractPriceDecimals(contract);
  assert.equal(decimals, 1, '0.5 Deribit tick requires one digit, not two');
  assert.equal(formatDomPrice(2355, decimals), '2,355.0');
  assert.equal(formatDomPrice(2354.5, decimals), '2,354.5');
  assert.equal(formatDomPrice(2354, decimals), '2,354.0');
  assert.equal(formatDomPrice(2353.5, decimals), '2,353.5');
  // Last/Mark/Index are formatted with same contract precision,
  // regardless of how many places those source numbers happen to have.
  assert.equal(formatDomPrice(2354.24, decimals), '2,354.2');
  assert.equal(formatDomPrice(2356.00001, decimals), '2,356.0');
});

test('options tick_size is specification-driven and preserves trailing zeros', () => {
  const decimals = contractPriceDecimals({
    name: 'ETH-25DEC26-3000-C',
    kind: 'option',
    tickSize: 0.0001,
    tickSizeSteps: [{ abovePrice: 0.01, tickSize: 0.0005 }]
  });
  assert.equal(decimals, 4);
  assert.equal(formatDomPrice(0.0016, decimals), '0.0016');
  assert.equal(formatDomPrice(0.001, decimals), '0.0010');
  assert.equal(formatDomPrice(0.0012, decimals), '0.0012');
  assert.equal(formatDomPrice(0.0008, decimals), '0.0008');
});

test('tick_size_steps may demand additional precision, not quote heuristics', () => {
  const spec = {
    name: 'ETH-25DEC26-3000-C',
    kind: 'option',
    tickSize: 0.0001,
    tickSizeSteps: [{ abovePrice: 0.001, tickSize: 0.00005 }]
  };
  assert.equal(contractPriceDecimals(spec), 5);
  assert.equal(formatDomPrice(0.001, 5), '0.00100');
  assert.equal(contractPriceDecimals({
    name: 'ETH-25DEC26-3000-P', kind: 'option',
    tickSize: 1e-8, tickSizeSteps: []
  }), 8);
});

test('missing or malformed contract spec cannot invent price decimals', () => {
  assert.equal(contractPriceDecimals(null), null);
  assert.equal(formatDomPrice(2_340.5, null), '—');
  assert.equal(contractPriceDecimals({
    name: 'ETH-PERPETUAL', kind: 'future', tickSize: 0, tickSizeSteps: []
  }), null);
  assert.equal(contractPriceDecimals({
    name: 'ETH-PERPETUAL', kind: 'future', tickSize: 0.5,
    tickSizeSteps: [{ abovePrice: 1, tickSize: -1 }]
  }), null);
});

test('native 1/10/20 depths and separate RAW display cap preserve the selected view', () => {
  const bids = Array.from({ length: 50 }, (_, i) => ({ price: 2500 - i, amount: 1 }));
  const asks = Array.from({ length: 50 }, (_, i) => ({ price: 2501 + i, amount: 1 }));
  for (const [mode, count] of [[1, 1], [10, 10], [20, 20], ['raw', 50]]) {
    const dom = toPriceLadder({ bids, asks }, mode);
    assert.equal(dom.bids.length, count);
    assert.equal(dom.asks.length, count);
    assert.equal(dom.bids[0].price, 2500);
    assert.equal(dom.asks[count - 1].price, 2501);
    assert.equal(dom.asks[0].price, 2500 + count);
    assert.equal(dom.bids[count - 1].price, 2501 - count);
    assert.equal(dom.spread.absolute, 1);
  }
  assert.equal(toPriceLadder({ bids, asks }).bids.length, 50,
    'Default RAW view displays up to fifty reconstructed levels per side.');
  assert.equal(toPriceLadder(null, 'raw').spread, null,
    'Invalid RAW feed cannot become valid merely by selecting RAW.');
});

test('own orders beyond ten levels never acquire invented public liquidity', () => {
  const bids = Array.from({ length: 50 }, (_, i) => ({ price: 2500 - i, amount: 1 }));
  const asks = Array.from({ length: 50 }, (_, i) => ({ price: 2501 + i, amount: 2 }));
  const own = [
    { direction: 'buy', price: 2500, amount: 3, orders: 1 },
    { direction: 'buy', price: 2480, amount: 4, orders: 2 },
    { direction: 'sell', price: 2526, amount: 7, orders: 1 },
    { direction: 'sell', price: 2600, amount: 9, orders: 1 }
  ];
  const dom10 = toPriceLadder({ bids, asks }, 10);
  const outside10 = ownOrdersOutsideDepth(own, dom10);
  assert.equal(outside10.length, 3);
  assert.ok(outside10.some(x => x.price === 2480 && x.direction === 'buy'));
  assert.ok(outside10.some(x => x.price === 2526 && x.direction === 'sell'));
  assert.ok(outside10.some(x => x.price === 2600));
  assert.ok(!outside10.some(x => x.price === 2500));

  const dom20 = toPriceLadder({ bids, asks }, 20);
  const outside20 = ownOrdersOutsideDepth(own, dom20);
  assert.deepEqual(outside20.map(x => x.price).sort((a, b) => a - b), [2480, 2526, 2600]);

  const raw = toPriceLadder({ bids, asks }, 'raw');
  assert.deepEqual(ownOrdersOutsideDepth(own, raw).map(x => x.price), [2600],
    'Beyond the first 50 RAW levels, own order remains only in the separate list.');
  assert.equal(raw.bids.length, 50);
  assert.equal(raw.asks.length, 50);
});

test('own price visibility must be both side- and price-exact', () => {
  const book = toPriceLadder({
    bids: [{ price: 101, amount: 4 }],
    asks: [{ price: 102, amount: 5 }]
  });
  const own = [
    { direction: 'sell', price: 101, amount: 1, orders: 1 },
    { direction: 'buy', price: 102, amount: 1, orders: 1 },
    { direction: 'sell', price: 102, amount: 1, orders: 1 }
  ];
  const outside = ownOrdersOutsideDepth(own, book);
  assert.equal(outside.length, 2);
  assert.ok(outside.some(x => x.direction === 'sell' && x.price === 101));
  assert.ok(outside.some(x => x.direction === 'buy' && x.price === 102));
});
