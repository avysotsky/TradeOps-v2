# TradeOps-v2 — Orchestrator Handoff 2026-10-22 (IBKR / potential client Daniel)

**Repository:** PRIVATE `avysotsky/TradeOps-v2`. **No merge/publish/live trading.**
**Registered main at start:** `c7a0d29028d7cf4958a26aa4879b99b3e39f7b14`.
**Worker branch:** `TradeOps/to-v2-06-ibkr-account-free-readiness`.
**TO-V2-06** draft PR: consult live GitHub for its number/current HEAD,
never trust a pinned SHA once new commits are made.

## Business reason

A lead signing "Daniel" wrote in English asking about a possible project
related to broker/exchange API integrations and order-execution reliability,
suggesting either a short call or **message exchange**, and asking about
availability and typical **fixed-scope** project prices. No statement of
work, exact broker, API type, budget, environment, timing, jurisdiction or
required order lifecycle has yet been supplied.

Do not assume this client specifically requests Interactive Brokers.
Prioritize concise async messaging and discovery: target brokers/exchanges,
Paper vs production access, order types/markets, new vs existing code,
auth/session constraints, trade-risk controls and environment, test fixtures,
delivery acceptance, budget and deadline. Do not request credentials or
make uncontrolled API calls. The user prefers written communication and
needs paid client work; propose a bounded paid discovery/first deliverable
instead of prematurely quoting an unbounded integration.

## Context and source

Current TradeOps-v2 .NET 10 (SDK 10.0.401) has real code:
`src/TradeOps.Infrastructure/Exchange/Ibkr/`:
`IbkrPaperExchangeClient`, `IbkrInstrumentResolver`, `IbkrOptions`,
`IbkrResolvedInstrument`. Existing
`tests/TradeOps.UnitTests/IbkrAdapterTests.cs` and a separately
opt-in `tools/TradeOps.IbkrPaperSmoke` exist. IBKR first slice reads
Paper account info, stocks, positions, orders and order status, and blocks
place/cancel. No owner IBKR real or Paper account exists; real Paper smoke
is NOT RUN.

**Official 2026 requirements:** IBKR TWS/IB Gateway and /iserver Web API
need authentication for account/market/trading; a generic trial/unauthenticated
public market data API must NOT be invented. Official Web API change
2026-09-09 **removed deprecated secType query from /iserver/secdef/search**.
`docs/ibkr/IBKR_ACCOUNT_FREE_DEVELOPMENT.md` links current official
sources. Search by symbol and verify STK/currency/exchange via contract info.

## New bounded implementation

Branch created off exact main. Changes:
1. Remove deprecated secType search parameter while verifying actual returned
   STK metadata; add negative xUnit 401 + mixed OPT/STK candidates and
   ensure query shape.
2. Deny localhost CPGW URL userinfo/query/fragment in adapter config,
   add 5 unsafe-URL cases.
3. New `tools/TradeOps.IbkrOfflineDemo` project and Program.cs:
   deterministic synthetic in-memory HttpMessageHandler makes
   zero external requests, drives existing Paper adapter through
   account/position/order/conid+symbol read paths, verifies non-Paper
   denial and local mutation block. Included in `TradeOps.sln`.
   Expected output starts `IBKR ACCOUNT-FREE OFFLINE DEMO PASS`.
   This is synthetic proof, NOT an authenticated broker connection.
4. `docs/ibkr/IBKR_ACCOUNT_FREE_DEVELOPMENT.md` formalizes limitations,
   demonstration command, next order reliability stages and claim boundaries.
5. This handoff: future worker entry point.

## Automation + safety

User's Ubuntu admin previously installed autonomous WSL2 runner checking
every NEW DRAFT TradeOps-v2 PR head. Results:
`avysotsky/TradeOps-Relay-Queue/autotest-results/tradeops/pr-<PR>/<FULL_SHA>.json`.
Do NOT use legacy tasks-v2/results-v2; DO NOT activate old disabled relays
or GitHub Actions. Host global concurrency=1 across JobRadar/ResidentChat/
TradeOps/DocFlow; isolated caches and cleanup; inspect exact latest SHA
and real test counts. **Historical** prior TradeOps PR #7
`31e5429ae5545cdda553c911288f7ed7eee8b647` had
576/576 PASS, but this is NOT proof for TO-V2-06.

No account: all remote IBKR API integration tests can only use synthetic
offline handlers. Never enable IBKR order placement or cancellation on a
live transport, even Paper until separately reviewed/accepted with a real
Paper login. No live broker orders, real account identifiers, credentials,
external market-data violations, secrets, production deployment, automatic
merge or NuGet publication.

## Next worker concrete actions

1. Fetch latest main, new branch/Draft PR, file diff and latest WSL2
   JSON for exact active head. Run independent diff review.
2. If WSL2 build/test FAIL, inspect source and sanitized result; fix in
   same branch/new commit, ensure exact SHA rechecked.
3. Independently qualify the new account-free CLI runtime via a trusted
   no-network runner or user-local dotnet 10 **only if necessary**; WSL2
   compilation by itself is not runtime proof.
4. Expand offline order-lifecycle reliability: model IBKR cOID, place-order
   preflight vs reply-chain handling, 401/403/429/5xx, reconnect/duplicate,
   partial-fill and cancel idempotency using FIXED local in-memory simulated
   transport, *without* flipping IBKR mutation capabilities.
5. Once exact-head WSL2 PASS, owner must approve private-main merge; do not
   ask the user to copy test output between OpenClaw and ChatGPT.
6. Customer follow-up: prepare an asynchronous English response stating
   availability (subject to user reconfirmation) and fixed-scope quote after
   a brief requirements exchange. A developer must not claim real IBKR
   connection or paper order execution without having exercised it.

## NuGet/DocFlow separation

Private DocFlow-v2 merged DF-V2-16 PR #22 on 2026-10-14, final main SHA
`1edc228e15d5c18cd8d92f84a828efa3d56c2f1b`. Prior DocFlow draft
PR #23 was read-only package source parity NO-GO. Do not conflate this
IBKR work with DocFlow NuGet release rights. GitHub Actions OFF.
