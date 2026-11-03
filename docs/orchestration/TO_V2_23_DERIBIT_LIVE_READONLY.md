# TO-V2-23 — Deribit production read-only account connectivity

**Stacked base:** qualified TO-V2-22 exact
`6e22ce45a49c5463c43b51aa7cdf7f39638de921`.
**Status:** Draft and offline test-only until owner runs explicit real-account
read-only acceptance. No mainnet order placement or cancellation.

## Rationale

The existing `DeribitTestnetExchangeClient` implements buy/sell/cancel, and
its options and WebSocket validation reject production. Never change its
endpoint to a production host or expose it via TradeOps.Api/Worker.

The new `DeribitLiveReadOnlyClient` is a distinct non-DI-registered
transport, deliberately **not** an `IExchangeClient`; it implements ONLY
the three allowed HTTPS GET requests to fixed `www.deribit.com:443`:
1. `/api/v2/private/get_account_summaries`
2. `/api/v2/private/get_positions?currency=any`
3. `/api/v2/private/get_open_orders`

Deribit officially supports HTTP Basic Auth for authenticated API methods.
Credentials are sent only in the HTTPS Authorization header (not URL),
without a token refresh request. The owner must create **a new, separate
production API key restricted to account:read, trade:read, wallet:none;
never allow trade:read_write, wallet:read_write or account:read_write**.
Never reuse a trading key for this smoke, and NEVER post a key in ChatGPT,
a PR, log or commit.

The disposable CLI `tools/TradeOps.DeribitLiveReadOnlySmoke` requires
`TRADEOPS_DERIBIT_LIVE_CONFIRM=READ_ONLY_MAINNET_THREE_CALLS` and
runtime-only `DERIBIT_LIVE_READONLY_CLIENT_ID` and
`DERIBIT_LIVE_READONLY_CLIENT_SECRET`. It disables redirects, cookie
storage and all automatic retries, performs exactly 3 sequential private
GET calls, and prints only counts (no account balances/IDs/order details).
Any invalid schema, auth failure, rate-limit or HTTP redirect terminates
with a sanitized failure message. No broker mutation code exists.

Do **not** run this CLI in CI, GitHub relay, docker tests or shared
environments. xUnit uses synthetic HttpMessageHandler only.

## Qualification gates

**Gate A:** independent WSL2 exact-SHA restore/build/unit tests PASS,
0 fails/skips and cleanup 6/6. This proves only offline transport
boundaries and compilation, NOT real Deribit connectivity.

**Gate B:** owner runs the CLI on his trusted Windows workstation after
locally creating the least-privilege API key. Required marker:
`TRADEOPS DERIBIT LIVE READONLY PASS account_currencies=N positions=N open_orders=N mutating_calls=0`.
No balance, key, account ID or order ID should be shared in transcripts.
Keep this gate blocked until owner supplies sanitized marker. Deribit
Testnet account is independent of the live account.

## Later separate workstreams

After authenticated read-only qualification: complete/correct normalizers
for options, futures, perpetual and spot with currency/multiplier semantics;
full coverage and idempotent order/execution evidence; WebSocket gap/reconnect
and snapshot continuity; scoped reconciliation persistence; testnet
order placement and cancellation under strict risk limits. Live order
placement requires **separate explicit owner approval** and risk controls;
a read-only connection is not trading readiness.

References:
- https://support.deribit.com/hc/en-us/articles/38507223482781-API-guidance
- https://deribit1692970334.zendesk.com/hc/en-us/articles/33675364222877-API-Authentication-Guide
- https://support.deribit.com/hc/en-us/articles/25944687755293-Features
