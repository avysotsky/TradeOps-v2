import test from 'node:test';import assert from 'node:assert/strict';
import {accountOrders,accountPositions,filterSpreads,spreadLegs,isValidMockBook,quoteLabel,instrumentFor,matrixSymbols,matrixQuote,invertSpread} from '../src/model.ts';import {accounts,orders,positions,spreads,makeBook,instruments} from '../src/fixtures.ts';
test('account-scoped order and position reads',()=>{assert.deepEqual(accountOrders(orders,accounts[0]).map(x=>x.orderId),['MOCK-CC-A-001','MOCK-CC-A-002']);assert.deepEqual(accountOrders(orders,accounts[1]).map(x=>x.orderId),['MOCK-CC-B-001']);assert.equal(accountPositions(positions,accounts[1]).length,1)});
test('spread filters and typed buy/sell legs',()=>{assert.equal(filterSpreads(spreads,'ETH',[]).length,2);assert.equal(filterSpreads(spreads,'BTC',['30 Oct']).length,2);const l=spreadLegs(spreads[0]);assert.equal(l[0].side,'BUY');assert.equal(l[1].side,'SELL');assert.notEqual(l[0].symbol,l[1].symbol)});
test('book sequence non-crossed for every fixture instrument',()=>{for(const i of instruments)assert.ok(isValidMockBook(makeBook(i),i))});
test('source-specific quote label and contract symbol',()=>{assert.notEqual(quoteLabel(spreads[0],'mark'),quoteLabel(spreads[0],'median'));assert.equal(instrumentFor(instruments,'spot','BTC').symbol,'BTC/USDT')});

test('expiry controls match explicit fixture expiry groups',()=>{assert.equal(filterSpreads(spreads,'BTC',['Weekly']).length,2);assert.equal(filterSpreads(spreads,'BTC',['Monthly']).length,4);assert.equal(filterSpreads(spreads,'ETH',['Weekly']).length,1);assert.equal(filterSpreads(spreads,'ETH',['Monthly']).length,1);assert.equal(filterSpreads(spreads,'BTC',['30 Oct']).every(q=>q.expiryGroup==='Monthly'),true);assert.equal(filterSpreads(spreads,'BTC',['Daily']).length,0);assert.equal(filterSpreads(spreads,'BTC',['Quarterly']).length,0);assert.equal(filterSpreads(spreads,'BTC',['Weekly','Monthly']).length,6)});

test('2D spread grid includes instruments across rows and columns with diagonal inert',()=>{
  const btc=filterSpreads(spreads,'BTC',[]);
  const symbols=matrixSymbols(btc);
  assert.ok(symbols.includes('BTC/USDT'));
  assert.ok(symbols.includes('BTCUSDT-Perp'));
  assert.equal(matrixQuote(btc,'BTC/USDT','BTC/USDT'),undefined);
  const q=matrixQuote(btc,'BTCUSDT-Perp','BTC-30OCT26');
  assert.equal(q?.id,'btc-perp-oct');
  const rev=matrixQuote(btc,'BTC-30OCT26','BTCUSDT-Perp');
  assert.equal(rev?.longSymbol,q?.shortSymbol);
  assert.equal(rev?.shortSymbol,q?.longSymbol);
  assert.equal(rev?.bidUsd,-q.askUsd);
  assert.equal(rev?.askUsd,-q.bidUsd);
  assert.equal(rev?.markSpreadUsd,-q.markSpreadUsd);
  assert.ok((rev?.bidUsd ?? 0)<=(rev?.askUsd ?? -1));
  assert.deepEqual(invertSpread(invertSpread(q)).longSymbol,q.longSymbol);
});
