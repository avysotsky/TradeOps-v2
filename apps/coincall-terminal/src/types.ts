export type Asset='BTC'|'ETH';export type Product='spot'|'futures'|'options';export type Side='BUY'|'SELL';export type AccountKey=Readonly<{venue:'CoinCall';environment:'mock';accountId:string}>;
export interface Account {key:AccountKey;label:string;equityUsd:number;availableUsd:number;initialMarginPercent:number;maintenanceMarginPercent:number;marginMode:'Cross'|'Isolated';balances:readonly Balance[]}
export interface Balance {symbol:string;equity:number;available:number;borrowed:number}
export interface Instrument {symbol:string;asset:Asset;product:Product;quoteCurrency:string;tickSize:number;amountStep:number;price:number;mark:number;index:number;fundingPercent?:number;expiry?:string}
export interface BookLevel {price:number;amount:number;total:number}
export interface Book {instrument:string;bids:readonly BookLevel[];asks:readonly BookLevel[];epoch:'fixture';asOf:string}
export interface MockOrder {accountId:string;orderId:string;instrument:string;side:Side;type:'Limit'|'Market';price:number;amount:number;filled:number;status:'NEW'|'PARTIALLY_FILLED'|'FILLED'|'CANCELED';createdAt:string}
export interface Position {accountId:string;symbol:string;side:'Long'|'Short';size:number;entry:number;mark:number;pnl:number;initialMargin:number;delta:number}
export interface SpreadQuote {id:string;asset:Asset;longSymbol:string;shortSymbol:string;tenor:string;bidUsd:number;askUsd:number;bidPercent:number;askPercent:number;fundingPercent:number;markSpreadUsd:number}
export interface ChartPoint {label:string;value:number}
