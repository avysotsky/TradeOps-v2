import type {Account,Book,Instrument,MockOrder,Position,SpreadQuote} from './types.ts';
export function accountOrders(rows:readonly MockOrder[],account:Account){return rows.filter(x=>x.accountId===account.key.accountId)}
export function accountPositions(rows:readonly Position[],account:Account){return rows.filter(x=>x.accountId===account.key.accountId)}
export function filterSpreads(quotes:readonly SpreadQuote[],asset:'BTC'|'ETH',filters:readonly string[]){return quotes.filter(q=>q.asset===asset&&(filters.length===0||filters.some(f=>f==='All'||q.tenor.includes(f))))}
export function formatUsd(n:number,decimals=2){return n.toLocaleString('en-US',{minimumFractionDigits:decimals,maximumFractionDigits:decimals})}
export function spreadLegs(q:SpreadQuote):readonly {symbol:string;side:'BUY'|'SELL'}[]{return [{symbol:q.longSymbol,side:'BUY'},{symbol:q.shortSymbol,side:'SELL'}]}
export function isValidMockBook(b:Book,i:Instrument){return b.epoch==='fixture'&&b.instrument===i.symbol&&b.bids.every((x,n,a)=>x.price>0&&x.amount>=0&&(n===0||a[n-1].price>x.price))&&b.asks.every((x,n,a)=>x.price>0&&x.amount>=0&&(n===0||a[n-1].price>x.price))&&b.bids[0].price<b.asks[b.asks.length-1].price}
export function quoteLabel(q:SpreadQuote,source:'mark'|'median'){return formatUsd(source==='mark'?q.markSpreadUsd:(q.bidUsd+q.askUsd)/2,1)}
export function instrumentFor(ins:readonly Instrument[],product:'spot'|'futures',asset:'BTC'|'ETH'){const found=ins.find(i=>i.product===product&&i.asset===asset&&(product==='spot'||i.symbol.includes('Perp')));if(!found)throw new Error('Missing instrument fixture');return found}
