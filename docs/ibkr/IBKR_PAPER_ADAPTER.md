# Interactive Brokers Paper Adapter — WS-01

## API surface decision

The first IBKR milestone uses the Interactive Brokers Web API through Client Portal Gateway (CPGW).

Why this surface is used first:

- TradeOps exchange adapters already use HTTP-oriented boundaries and `IExchangeClient`.
- Web API exposes contract discovery, account data, positions, order monitoring and trading.
- IBKR Paper accounts are supported by the Web API.
- CPGW is suitable for an individual-client Paper MVP without introducing the TWS Java/C++-style callback SDK into TradeOps.

TWS API / IB Gateway remains an alternative for a later deployment-specific milestone if a client requires its native event model or operational characteristics.

## CPGW constraints

The adapter assumes:

- base URL `https://localhost:5000/v1/api`;
- browser authentication is completed on the same machine as CPGW;
- API calls are made from the same machine;
- one IB username can have only one active brokerage session at a time;
- the session is kept alive externally (IBKR recommends regular `/tickle`);
- CPGW's default self-signed localhost certificate may be accepted only by the dedicated IBKR HttpClient.

## Paper-only safety boundary

The adapter fails closed in two layers:

1. configuration accepts only the local CPGW endpoint;
2. every account/read lifecycle begins with `GET /iserver/accounts` and requires `isPaper=true`.

If the authenticated brokerage session is live, the adapter refuses it.

The first slice deliberately advertises no order-placement or cancellation capability. Mutations throw locally until reply-confirmation handling, retry/idempotency semantics and ambiguous-placement recovery are implemented and validated against an actual IBKR Paper account.

## Instrument resolution

The existing frozen `InstrumentReference v1` is reused unchanged.

First milestone supports:

```text
AssetClass = Stock
Symbol
Currency
VenueInstrumentId = IBKR conid (optional)
Exchange = SMART or listing/routing exchange (optional)
```

When `VenueInstrumentId` is present, the conid is validated against IBKR contract metadata.

When it is absent, the adapter searches the IBKR security-definition API and resolves a unique stock contract using symbol plus optional currency/exchange. Ambiguous results require the caller to provide a conid.

## Read-path mapping

IBKR data is normalized into the existing TradeOps lifecycle:

- account summary -> `AccountInfo`;
- stock positions -> `Position`;
- live/recent orders -> `Order`;
- specific order lookup -> existing `Order` lifecycle state.

No IBKR-specific parallel trading domain is introduced.

## First-slice endpoints

```text
GET /iserver/accounts
GET /iserver/secdef/search
GET /iserver/contract/{conid}/info
GET /iserver/account/{accountId}/summary
GET /portfolio/accounts
GET /portfolio2/{accountId}/positions
GET /iserver/account/orders
GET /iserver/account/order/status/{orderId}
POST /iserver/account   (only for selecting among multiple Paper accounts)
```

## Deferred mutation slice

Before enabling MARKET/LIMIT in capability metadata, implement and paper-test:

- deterministic TradeOps client ID -> IBKR `cOID` semantics;
- `POST /iserver/account/{accountId}/orders`;
- all required `/iserver/reply/{replyId}` confirmation chains;
- cancellation;
- ambiguous placement recovery;
- reconciliation after reconnect/session reset;
- execution/fill ingestion strategy;
- pacing-aware retry behavior.

Live execution remains out of scope.


## Real Paper read-path manual smoke

A real authenticated Paper validation is currently an external dependency. The repository owner does not currently have an IBKR/Paper account, so this section prepares the future validation but does not claim it has happened.

The opt-in harness is:

```text
tools/TradeOps.IbkrPaperSmoke
```

The normal solution build compiles this project, but ordinary CI does not execute it. The harness never calls order placement or cancellation. It first checks that the `IbkrPaper` capability profile still has both mutation capabilities disabled.

### Start Client Portal Gateway

Prerequisites:

1. Have an IBKR account with a Paper Trading account available.
2. Install a working Java runtime and verify it with `java -version`.
3. Download and unzip the Interactive Brokers Client Portal Gateway.
4. Open a terminal in the extracted `clientportal.gw` directory.
5. On Windows run:

```text
bin\run.bat root\conf.yaml
```

On Unix/macOS run:

```text
bin/run.sh root/conf.yaml
```

Keep the gateway terminal open.

### Authenticate specifically to Paper

On the same machine where CPGW is running:

1. Log out of other active IBKR brokerage sessions for that username.
2. Open `https://localhost:5000` in a browser.
3. Complete the browser authentication using the **Paper Trading account credentials/session**, not Live.
4. The browser login is manual; do not automate it and do not place credentials, cookies or tokens in TradeOps configuration.
5. Run the smoke from that same machine.

The production adapter calls `GET /iserver/accounts` and requires `isPaper == true`. A Live brokerage session is rejected before the remainder of the read-path validation.

### Runtime environment

Required explicit opt-in:

```text
TRADEOPS_IBKR_PAPER_SMOKE_CONFIRM=RUN_PAPER_READ_ONLY_SMOKE
```

Optional runtime-only values:

```text
TRADEOPS_IBKR_PAPER_ACCOUNT_ID
TRADEOPS_IBKR_PAPER_ACCOUNT_CURRENCY
TRADEOPS_IBKR_PAPER_SMOKE_SYMBOL
TRADEOPS_IBKR_PAPER_SMOKE_CURRENCY
TRADEOPS_IBKR_PAPER_SMOKE_CONID
TRADEOPS_IBKR_PAPER_SMOKE_EXCHANGE
TRADEOPS_IBKR_PAPER_SMOKE_ORDER_ID
```

`TRADEOPS_IBKR_PAPER_ACCOUNT_ID` is needed only when an explicit account must be selected. Never commit its real value.

The public instrument defaults are:

```text
symbol=AAPL
currency=USD
conid=265598
exchange=<not forced>
account currency=USD
```

### Windows PowerShell command

From the TradeOps repository root:

```powershell
$env:TRADEOPS_IBKR_PAPER_SMOKE_CONFIRM="RUN_PAPER_READ_ONLY_SMOKE"
$env:TRADEOPS_IBKR_PAPER_ACCOUNT_ID="<paper-account-id>" # optional/runtime only
$env:TRADEOPS_IBKR_PAPER_ACCOUNT_CURRENCY="USD"
$env:TRADEOPS_IBKR_PAPER_SMOKE_SYMBOL="AAPL"
$env:TRADEOPS_IBKR_PAPER_SMOKE_CURRENCY="USD"
$env:TRADEOPS_IBKR_PAPER_SMOKE_CONID="265598"
# $env:TRADEOPS_IBKR_PAPER_SMOKE_EXCHANGE="SMART"       # optional
# $env:TRADEOPS_IBKR_PAPER_SMOKE_ORDER_ID="<paper-order-id>" # optional

dotnet run --project tools/TradeOps.IbkrPaperSmoke --configuration Release
```

The values in angle brackets are placeholders. Real account/order identifiers remain local runtime values.

### Bash command

```bash
export TRADEOPS_IBKR_PAPER_SMOKE_CONFIRM=RUN_PAPER_READ_ONLY_SMOKE
export TRADEOPS_IBKR_PAPER_ACCOUNT_ID="<paper-account-id>" # optional/runtime only
export TRADEOPS_IBKR_PAPER_ACCOUNT_CURRENCY=USD
export TRADEOPS_IBKR_PAPER_SMOKE_SYMBOL=AAPL
export TRADEOPS_IBKR_PAPER_SMOKE_CURRENCY=USD
export TRADEOPS_IBKR_PAPER_SMOKE_CONID=265598
# export TRADEOPS_IBKR_PAPER_SMOKE_EXCHANGE=SMART
# export TRADEOPS_IBKR_PAPER_SMOKE_ORDER_ID="<paper-order-id>"

dotnet run --project tools/TradeOps.IbkrPaperSmoke --configuration Release
```

### Expected PASS output

Counts and public instrument details vary, but a successful run has this shape:

```text
Provider: IbkrPaper
Paper session confirmed: PASS
Account selected: PASS
Account summary retrieved: PASS
Positions count: <number>
Instrument resolution (known conid): PASS <symbol>/<currency> conid=<public-conid>
Instrument resolution (symbol+currency): PASS <symbol>/<currency> conid=<public-conid>
Open orders count: <number>
Order lookup: SKIPPED (no runtime order id supplied)
REAL IBKR PAPER READ-PATH SMOKE: PASS
```

If `TRADEOPS_IBKR_PAPER_SMOKE_ORDER_ID` is supplied and resolves, the lookup line is:

```text
Order lookup: PASS status=<TradeOps-order-status>
```

The harness intentionally does not print account IDs, balances, usernames, credentials, cookies or authentication material.

### Fail-closed conditions

Any of the following invalidates the smoke and must end in `REAL IBKR PAPER READ-PATH SMOKE: FAIL`:

- explicit opt-in value is absent or incorrect;
- CPGW is not reachable at `https://localhost:5000/v1/api`;
- browser/brokerage authentication is missing or expired;
- `GET /iserver/accounts` does not confirm `isPaper == true`;
- the configured Paper account is not available in the authenticated session;
- multiple-account selection cannot be resolved safely;
- account summary or positions cannot be read;
- the known conid does not match the requested stock identity;
- symbol/currency/exchange resolution is missing or ambiguous;
- open orders cannot be read;
- a supplied Paper order ID cannot be found;
- `IbkrPaper` capability metadata has placement or cancellation enabled.

Until this command is run successfully against a real authenticated Paper session, WS-01 remains `WAITING_FOR_REAL_PAPER_ACCOUNT` and mutation work remains blocked.
