import type {Account,Book,Instrument,MockOrder,Position,SpreadQuote,ChartPoint} from './types.ts';
export const FIXTURE_AS_OF='2026-10-25T12:00:00.000Z';
export const accounts:readonly Account[]=[
{key:{venue:'CoinCall',environment:'mock',accountId:'demo-alpha'},label:'DEMO · Alpha',equityUsd:124850.73,availableUsd:90520.25,initialMarginPercent:14.8,maintenanceMarginPercent:6.4,marginMode:'Cross',balances:[{symbol:'USDT',equity:85120.73,available:65100.25,borrowed:0},{symbol:'BTC',equity:0.287,available:0.187,borrowed:0},{symbol:'ETH',equity:4.4,available:3.9,borrowed:0}]},
{key:{venue:'CoinCall',environment:'mock',accountId:'demo-beta'},label:'DEMO · Beta',equityUsd:38740.16,availableUsd:34380.16,initialMarginPercent:7.1,maintenanceMarginPercent:2.3,marginMode:'Isolated',balances:[{symbol:'USDT',equity:28740.16,available:26380.16,borrowed:0},{symbol:'BTC',equity:0.08,available:0.08,borrowed:0}]}];
export const instruments:readonly Instrument[]=[
{symbol:'BTC/USDT',asset:'BTC',product:'spot',quoteCurrency:'USDT',tickSize:0.1,amountStep:0.0001,price:68125.4,mark:68124.9,index:68123.2},
{symbol:'ETH/USDT',asset:'ETH',product:'spot',quoteCurrency:'USDT',tickSize:0.01,amountStep:0.001,price:3516.42,mark:3515.8,index:3514.88},
{symbol:'SOL/USDT',asset:'SOL',product:'spot',quoteCurrency:'USDT',tickSize:0.001,amountStep:0.01,price:188.527,mark:188.518,index:188.505},
{symbol:'BTCUSDT-Perp',asset:'BTC',product:'futures',quoteCurrency:'USDT',tickSize:0.1,amountStep:0.001,price:68148.6,mark:68146.5,index:68123.2,fundingPercent:0.0083},
{symbol:'ETHUSDT-Perp',asset:'ETH',product:'futures',quoteCurrency:'USDT',tickSize:0.01,amountStep:0.01,price:3518.12,mark:3516.7,index:3514.88,fundingPercent:0.012},
{symbol:'BTC-30OCT26',asset:'BTC',product:'futures',quoteCurrency:'USDT',tickSize:0.1,amountStep:0.001,price:68224.6,mark:68222.7,index:68123.2,expiry:'30 Oct 2026'},
{symbol:'BTC-06NOV26',asset:'BTC',product:'futures',quoteCurrency:'USDT',tickSize:0.1,amountStep:0.001,price:68292.6,mark:68291.3,index:68123.2,expiry:'06 Nov 2026'},
{symbol:'BTC-27NOV26',asset:'BTC',product:'futures',quoteCurrency:'USDT',tickSize:0.1,amountStep:0.001,price:68380.2,mark:68372.3,index:68123.2,expiry:'27 Nov 2026'},
{symbol:'ETH-06NOV26',asset:'ETH',product:'futures',quoteCurrency:'USDT',tickSize:0.01,amountStep:0.01,price:3529.21,mark:3528.04,index:3514.88,expiry:'06 Nov 2026'},
{symbol:'ETH-30OCT26',asset:'ETH',product:'futures',quoteCurrency:'USDT',tickSize:0.01,amountStep:0.01,price:3524.32,mark:3523.1,index:3514.88,expiry:'30 Oct 2026'}];
export const orders:readonly MockOrder[]=[
{accountId:'demo-alpha',orderId:'MOCK-CC-A-001',instrument:'BTCUSDT-Perp',side:'BUY',type:'Limit',price:67950,amount:0.08,filled:0,status:'NEW',createdAt:'11:42:08'},
{accountId:'demo-alpha',orderId:'MOCK-CC-A-002',instrument:'BTC/USDT',side:'SELL',type:'Limit',price:68400,amount:0.04,filled:0.01,status:'PARTIALLY_FILLED',createdAt:'11:16:42'},
{accountId:'demo-beta',orderId:'MOCK-CC-B-001',instrument:'ETHUSDT-Perp',side:'SELL',type:'Limit',price:3560,amount:1.2,filled:0,status:'NEW',createdAt:'11:51:26'}];
export const positions:readonly Position[]=[
{accountId:'demo-alpha',symbol:'BTCUSDT-Perp',side:'Long',size:0.25,entry:67320.1,mark:68146.5,pnl:206.6,initialMargin:680.9,delta:0.25},
{accountId:'demo-alpha',symbol:'ETHUSDT-Perp',side:'Short',size:1.8,entry:3576.2,mark:3516.7,pnl:107.1,initialMargin:633,delta:-1.8},
{accountId:'demo-beta',symbol:'BTCUSDT-Perp',side:'Long',size:0.06,entry:67980,mark:68146.5,pnl:9.99,initialMargin:408.8,delta:0.06}];
export const spreads:readonly SpreadQuote[]=[
{id:'btc-perp-oct',asset:'BTC',longSymbol:'BTCUSDT-Perp',shortSymbol:'BTC-30OCT26',tenor:'30 Oct',expiryGroup:'Monthly',bidUsd:-80.1,askUsd:-67.6,bidPercent:-0.118,askPercent:-0.099,fundingPercent:0.0083,markSpreadUsd:-76.2},
{id:'btc-perp-week',asset:'BTC',longSymbol:'BTCUSDT-Perp',shortSymbol:'BTC-06NOV26',tenor:'06 Nov',expiryGroup:'Weekly',bidUsd:-146.5,askUsd:-135.2,bidPercent:-0.215,askPercent:-0.198,fundingPercent:0.0083,markSpreadUsd:-142.7},
{id:'btc-perp-nov',asset:'BTC',longSymbol:'BTCUSDT-Perp',shortSymbol:'BTC-27NOV26',tenor:'27 Nov',expiryGroup:'Monthly',bidUsd:-236.1,askUsd:-224.8,bidPercent:-0.346,askPercent:-0.329,fundingPercent:0.0083,markSpreadUsd:-225.8},
{id:'btc-spot-perp',asset:'BTC',longSymbol:'BTC/USDT',shortSymbol:'BTCUSDT-Perp',tenor:'Perp',expiryGroup:'Perpetual',bidUsd:-27.4,askUsd:-17.1,bidPercent:-0.04,askPercent:-0.025,fundingPercent:0.0083,markSpreadUsd:-21.6},
{id:'btc-spot-oct',asset:'BTC',longSymbol:'BTC/USDT',shortSymbol:'BTC-30OCT26',tenor:'30 Oct',expiryGroup:'Monthly',bidUsd:-109.1,askUsd:-88,bidPercent:-0.16,askPercent:-0.129,fundingPercent:0,markSpreadUsd:-97.8},
{id:'btc-spot-week',asset:'BTC',longSymbol:'BTC/USDT',shortSymbol:'BTC-06NOV26',tenor:'06 Nov',expiryGroup:'Weekly',bidUsd:-171.5,askUsd:-159.4,bidPercent:-0.252,askPercent:-0.234,fundingPercent:0,markSpreadUsd:-166.4},
{id:'btc-spot-nov',asset:'BTC',longSymbol:'BTC/USDT',shortSymbol:'BTC-27NOV26',tenor:'27 Nov',expiryGroup:'Monthly',bidUsd:-258.3,askUsd:-238.5,bidPercent:-0.379,askPercent:-0.35,fundingPercent:0,markSpreadUsd:-249.1},
{id:'eth-spot-perp',asset:'ETH',longSymbol:'ETH/USDT',shortSymbol:'ETHUSDT-Perp',tenor:'Perp',expiryGroup:'Perpetual',bidUsd:-3.6,askUsd:-1.1,bidPercent:-0.102,askPercent:-0.031,fundingPercent:0.012,markSpreadUsd:-0.9},
{id:'eth-perp-week',asset:'ETH',longSymbol:'ETHUSDT-Perp',shortSymbol:'ETH-06NOV26',tenor:'06 Nov',expiryGroup:'Weekly',bidUsd:-12.1,askUsd:-9.7,bidPercent:-0.344,askPercent:-0.276,fundingPercent:0.012,markSpreadUsd:-11.3},
{id:'eth-perp-oct',asset:'ETH',longSymbol:'ETHUSDT-Perp',shortSymbol:'ETH-30OCT26',tenor:'30 Oct',expiryGroup:'Monthly',bidUsd:-8.8,askUsd:-5.6,bidPercent:-0.25,askPercent:-0.159,fundingPercent:0.012,markSpreadUsd:-6.4}];
export const chart:readonly ChartPoint[]=[{label:'09:00',value:-89},{label:'09:30',value:-95},{label:'10:00',value:-85},{label:'10:30',value:-75},{label:'11:00',value:-81},{label:'11:30',value:-70},{label:'12:00',value:-76}];
export function makeBook(instrument:Instrument):Book{const digits=(String(instrument.tickSize).split('.')[1]||'').length;const round=(n:number)=>Number(n.toFixed(digits));const asks=Array.from({length:11},(_,i)=>({price:round(instrument.price+instrument.tickSize*(11-i)),amount:Number((0.11+(i%4)*0.045).toFixed(3)),total:Number(((i+1)*0.16).toFixed(3))}));const bids=Array.from({length:11},(_,i)=>({price:round(instrument.price-instrument.tickSize*(i+1)),amount:Number((0.14+(i%3)*0.061).toFixed(3)),total:Number(((i+1)*0.19).toFixed(3))}));return {instrument:instrument.symbol,bids,asks,epoch:'fixture',asOf:FIXTURE_AS_OF}}
