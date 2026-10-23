TO-V2-26 Deribit bounded read-only execution history

Base: frozen TO-V2-25 exact SHA 98b600d1b30ae3736bc6fd1d00421850dd782505.
Official endpoint: private/get_user_trades_by_currency_and_time.
Parameters: currency, start_timestamp, end_timestamp, count <= 1000, sorting,
historical. Scope trade:read.
Official API:
https://docs.deribit.com/api-reference/trading/private-get_user_trades_by_currency_and_time
Historical indexing guide:
https://docs.deribit.com/articles/accessing-historical-trades-orders

Scope:
- Existing isolated non-IExchangeClient mainnet read-only client adds ONE
  whitelist GET to the official user-trades-by-currency-and-time endpoint.
- DeribitTradeHistoryCollector validates uppercase currency, 30-day maximum
  window, max 1000 records/page, max 32 calls/mode and max 10000 trades/mode.
  When has_more, bisect inclusive millisecond time windows and deduplicate
  trade IDs ONLY when all normalized values are identical. Abort conflicts,
  single-millisecond overflow, budget exhaustion and malformed scope.
- Original position and open-order read-only endpoints are unchanged.
- CLI TradeOps.DeribitHistoryReadOnlySmoke requires explicit opt-in and
  outputs only counts of historical/recent trades, fetched pages and
  duplicate boundary IDs. It never emits actual trade IDs, instrument
  names, order IDs, prices, fees or full JSON.
- Windows owner helper reads EXISTING CurrentUser DPAPI encrypted vault
  only after verifying exact GitHub SHA and compiling the new CLI. No
  prompt to reenter stored secrets. Runs only on deliberate owner action.
- No order place/cancel/transfer, API/Worker DI change or PostgreSQL
  migration. Do not grant trade:read_write, wallet or new API key rights.

Important Deribit semantic limit:
historical=false returns recent trades (roughly 24h);
historical=true uses delayed indexing and excludes the newest records.
Even both indexes exhausted do NOT prove lifetime execution completeness
or freedom from indexing gaps/subaccount omissions. All output explicitly
marks history_complete=false.

Qualification:
Independent WSL2 exact-SHA Gate A and cleanup 6/6 are required before
any new real-account history endpoint request. Separate Windows owner
Gate B required with sanitized aggregate results only. Draft/unmerged.
