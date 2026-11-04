# Deribit public instrument manifest preflight

Part of [B3 capture qualification](DERIBIT_READONLY_CAPTURE_HOST.md). The separate
connector module owns `DeribitInstrumentCatalog`; it does not depend on persistence,
Worker, UI or legacy Infrastructure adapters.

## Data contract

One `ReadAsync(environment, currency, kind)` makes one fixed HTTPS GET to
`public/get_instruments` with `expired=false` and explicit `future` or `option` kind.
Testnet and mainnet have distinct official origins. Arbitrary hosts, `any`, combos,
spot, credential-bearing default headers and malformed currency tokens are rejected.
Currency symbols are not a hardcoded list: a valid public symbol can have an empty
result. Use current venue currency discovery when choosing the query groups.

The reader bounds the entire operation to 15 seconds, the decoded JSON to 8 MiB,
JSON depth to 32 and each result to 4,096 unique native instruments. HTTP/RPC errors,
bad metadata, duplicate names or unknown amount semantics fail the whole snapshot;
no partial success or raw upstream exception escapes. Caller cancellation is preserved.
Production callers must use a redirect/cookie-free HttpClient without credentials.

Each immutable snapshot retains query environment/currency/kind, receive time, native
name, explicit base/quote/settlement currencies, instrument type and active flag.
It does not split symbols or use contract size as `amount`. Quantity rules are:

| Instrument metadata | Native private-event amount |
|---|---|
| future + reversed, USD quote, settlement in base currency | USD |
| future + linear, including linear perpetual | base currency coin |
| option + reversed or linear | base currency coin |

These rules follow [instrument metadata](https://docs.deribit.com/api-reference/market-data/public-get_instruments)
and [native amount semantics](https://docs.deribit.com/api-reference/trading/private-buy).
Unsupported inverse combinations fail closed rather than guessing. Inactive entries
are retained: an inactive flag does not justify losing metadata for a late event.
Recently expired instruments and historical replay require a separate metadata path.

Query grouping, settlement currency and amount currency are separate fields. The
[venue's currency guide](https://docs.deribit.com/articles/currency-parameter)
distinguishes their meanings. `ValidatePlan` checks explicitly configured native
names and amount units/currencies against the matching query group and environment.
It never assigns subscription currency or account identity from a symbol or from
settlement currency. Duplicate query groups are ambiguous and rejected. Different
groups are separate observations at their own times, not an atomic venue snapshot.

The helper is an explicit preflight API, not automatically registered in the running
capture host. It does not replace/fill in trusted server configuration. It checks
configured entries, not whether every account instrument or possible future event
is covered. New listings, expired instruments, gaps and unknown private records
still require the existing RECOVERING/reconciliation behavior.

## Public-only operator tool

Build with the installed SDK; run the already built executable:

```powershell
D:\DotNet\dotnet.exe tools/TradeOps.DeribitInstrumentManifest/bin/Release/net10.0/TradeOps.DeribitInstrumentManifest.dll Mainnet BTC future
D:\DotNet\dotnet.exe tools/TradeOps.DeribitInstrumentManifest/bin/Release/net10.0/TradeOps.DeribitInstrumentManifest.dll Mainnet BTC option
```

Output contains public group counts, amount-unit counts, receive time and a digest
of the sorted metadata. There are no private calls, vault access, DB access, keys,
orders, positions or account identifiers. `PUBLIC_METADATA_PASS` proves only that
query and normalization; `accountCoverageQualified=false` is always explicit.

Targeted offline check in Ubuntu, outside global.json, after the bounded Windows
build of the existing test project:

```bash
cd /tmp
dotnet vstest /mnt/d/Projects/TradeOps-v2/tests/TradeOps.UnitTests/bin/Release/net10.0/TradeOps.UnitTests.dll \
  --Settings:/mnt/d/Projects/TradeOps-v2/scripts/testing/oms-postgres.runsettings \
  '--TestCaseFilter:FullyQualifiedName~DeribitInstrumentCatalogTests|FullyQualifiedName~DeribitPrivateCaptureParserTests'
```

Use Resource Guard before/after and bounded CPU as in the other local acceptance
runs. This selection has 35 cases and requires no PostgreSQL or credentials.
