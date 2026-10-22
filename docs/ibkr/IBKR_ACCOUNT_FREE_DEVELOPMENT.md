# Interactive Brokers — Account-Free Development Readiness (TO-V2-06)

**Scope:** PRIVATE `avysotsky/TradeOps-v2`, IBKR Web API via the existing
`IbkrPaperExchangeClient`. No live/real/paper login available to the owner
at this stage. **No broker/exchange order may be sent.**

## Real access limits, verified against IBKR official docs 2026-10-22

- TWS socket API requires a running **authenticated** TWS or IB Gateway
  session. TWS/IBG cannot be treated as an account-free public data proxy.
  https://www.interactivebrokers.com/docs/tws-api/doc/architecture/the-trader-workstation/the-ib-gateway
- Web API `/iserver` market data, contract discovery, orders and account
  lifecycle require an authorized brokerage session. An unauthenticated
  call to the published `api.ibkr.com` URL is NOT a supported public market
  data feed.
  https://www.interactivebrokers.com/docs/web-api/authentication/sessions
- A standard Paper Trading account requires onboarding to IBKR's brokerage
  account ecosystem; availability to newly approved/unfunded accounts can
  vary. A browser/demo trial must not be presumed API-capable. Never
  solicit IBKR usernames/passwords, session cookies or API keys here.
  https://www.interactivebrokers.com/campus/trading-lessons/how-to-open-an-ibkr-paper-trading-account/
- Current Web API **2026-09-09** changelog removed/deprecated the
  `secType` parameter on `/iserver/secdef/search`. Query only by symbol,
  then independently validate returned `STK` contract metadata, ticker,
  currency and exchange. Contract lookup remains **login-dependent**.
  https://www.interactivebrokers.com/docs/web-api/changelog/2026/9/9

## Existing integration capabilities from TradeOps-v2 main

- Native provider key: `IbkrPaper`, no new parallel business domain.
- Read-only Web API account summary, positions, open orders, order lookup,
  known `conid` validation and symbol-based stock discovery.
- Scoped local CPGW endpoint `https://localhost:5000/v1/api`.
- Order place/cancel disabled at local client method level and capability
  catalog. No account/order token in tests or output.
- Existing real-paper smoke at `tools/TradeOps.IbkrPaperSmoke` needs
  external authorized Paper login. It is **NOT RUN** without credentials.

## TO-V2-06 deliverables without an account

1. Repair the 2026-09-09 `secType` API drift in
   `IbkrInstrumentResolver`, keep strict `Stock` metadata filtering.
2. Reject unsafe CPGW base URLs containing username/password, query or
   fragments; retain HTTPS localhost port 5000 only.
3. Expand xUnit negative tests for retired parameter, search candidates
   of mixed security classes, HTTP 401, and unsafe endpoint origin.
4. Buildable self-contained CLI **account-free demonstration** at
   `tools/TradeOps.IbkrOfflineDemo` using the REAL existing adapter via
   an in-memory `HttpMessageHandler`. The stub strictly allows only
   explicit GET requests and contains no real account info or transport
   sockets. It verifies synthetic Paper session, account/positions/orders,
   conid and symbol resolution, rejection of a non-Paper response, and
   blocked order mutation. No IBKR authentication or network access.
5. Maintain default `IbkrPaper` capabilities as **read-only**. Do not
   introduce a new `IbkrLive` provider without owner authorization and
   actual broker-account acceptance tests.

## Account-free demonstration

From the repository root on a machine with **.NET SDK 10.0.401**:

```bash
dotnet run --project tools/TradeOps.IbkrOfflineDemo --configuration Release
```

Expected success starts with `IBKR ACCOUNT-FREE OFFLINE DEMO PASS` and
contains `external_requests=0 real_broker_connection=false`.
This demonstrates parsing/mapping/validation against **synthetic** canned
IBKR-shaped responses, NOT network connection or IBKR compatibility
certification. The project is included in the TradeOps solution for
compile-time verification. For the real Paper smoke and opt-in requirements,
see `docs/ibkr/IBKR_PAPER_ADAPTER.md`.

## E2E qualification process

Every new head of a **Draft PR** is picked up by the already installed WSL2
TradeOps runner. The result of the exact current head must be read from:

`avysotsky/TradeOps-Relay-Queue/autotest-results/tradeops/pr-<PR>/<FULL_SHA>.json`

Never use old `tasks-v2/results-v2`, invoke GitHub Actions, report previous
576/576 as proof for the new head or ask owner/admin to shuttle logs.
Restore/build/test must PASS with zero failures, complete cleanup and
actual test count. The **offline demo executable** is not proven runtime
PASS solely by a successful build or unit test; report it separately as
NOT RUN unless a trusted runner actually invokes it. No merges by a
worker without explicit owner decision.

## Customer-relevant next stages (separately scoped)

- **Order-execution reliability**: deterministic IBKR `cOID` and order
  preview/reply chain **parser only** first; rejection, duplicate IDs,
  reconnect/delayed ACK, partial-fill reconciliation and cancel/replace
  state-machine tests, all through synthetic HTTP/TWS event traces. No
  order-mutation capability should be advertised without real Paper validation.
- Multi-account selection; multi-asset contracts (options/futures)
  require explicit IBKR contract ambiguity/multiplier/expiry rules.
- Onboarding path if a real authorized Paper account becomes available:
  isolate credentials, prove Paper status and data subscription, run existing
  real-paper read-only smoke, then separately approve paper-only order
  mutations with explicit safety gates. Live Trading stays disabled.

**Honest portfolio claim:** "Built an account-free, deterministic,
read-only IBKR Web API adapter replay with stock contract resolution,
account/position/order normalization, negative security tests and
paper-session enforcement." Do NOT claim connected to IBKR market servers
or live/paper execution when no authenticated session has been tested.
