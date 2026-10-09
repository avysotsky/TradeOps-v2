# ARCH-03 — TradeOps-Owned DocFlow Port + HTTP Adapter

## Status
INTEGRATED — PR #73 squash-merged; exact-head CI passed; post-merge CI unverified

## Baselines
- Repository: avysotsky/TradeOps
- Branch: `TradeOps/arch03-docflow-http-adapter`
- Initial TradeOps main: `c4c349ee94e37708ef9a2ee9c0bcfc9813e33c11`
- DocFlow ARCH-01 main: `d6de1b5168c156b107cb3c3d71ef29983401ad40`
- ARCH-02 integration: `c0d589c91d49d4cffda1deb99dc9ba0ea4e4ca3d`, PR #72
- Canonical architectural plan: `docs/orchestration/DUAL_TOPOLOGY_ARCHITECTURE_PLAN.md`

## Goal
Define a **TradeOps-owned** application port for document normalization + schema extraction and implement a versioned HTTP adapter over the existing DocFlow HTTP contract. Avoid direct DocFlow dependencies in TradeOps.Domain or trade/risk logic. The same port will later have an in-process implementation (ARCH-04).

## Proven external HTTP contract
- DocFlow host `POST /api/v1/extractions`.
- Request fields in JSON: `schemaVersion: 1`, `rawDocument`, `schemaRequest`, `provider: openai|groq`, explicit `model`, optional `documentName`.
- Success: `schemaVersion: 1`, `normalizedDocument`, `structuredResult` using DocFlow existing canonical schema and normalization/extraction DTO field names.
- Base URL configured explicitly; credentials for LLM provider belong only to DocFlow server environment, not TradeOps.
- DocFlow body limit: 1 MiB. Client must apply its own response-size and deadline limits.

## Mandatory discovery before coding
1. Fetch current TradeOps main, DocFlow main, own branch SHA/compare, open workstreams and exact CI.
2. Read `DUAL_TOPOLOGY_ARCHITECTURE_PLAN.md`, `ORCHESTRATION.md`, `WORKSTREAM_PROTOCOL.md`, ARCH-01 specification in DocFlow and ARCH-02 integration record.
3. Inspect frozen TradeOps DocFlow adapters and raw schema shapes; preserve evidence, normalized fingerprint, segment IDs, provider engine and validation status.
4. Propose narrow application port and request/result model semantics first. No document-acquisition scraping or provider logic in TradeOps.
5. Verify existing DocFlow API has **no service-to-service authentication**. For ARCH-03, prohibit public-network deployment of unauthenticated extraction endpoint; local/private-network-only integration is permitted. Document server-to-server authentication as a deployment hardening blocker rather than silently exposing sensitive documents.

## Implementation
- Application owns port and typed, versioned boundary; infrastructure owns `HttpClient` adapter and DI composition.
- Bound HTTP URL, timeout/cancellation, response size, invalid JSON/schema, unexpected status codes, sanitized failures.
- Only explicitly configured internal DocFlow base addresses allowed at first; avoid arbitrary caller-supplied URLs and SSRF.
- Port is provider-neutral. No API key/secret passthrough DTOs.
- HTTP adapter does not return raw provider exceptions, transcript/body data to logging.
- Reuse canonical DocFlow schema DTO semantics without cloning its Python implementation; do not materialize documents on disk by default.
- No changes to existing CLI child-process regression path until ARCH-05 equivalence proven.
- No broker mutation or order submission.

## Acceptance
1. TradeOps application port independent of HTTP implementation and TradeOps.Domain unaffected.
2. HTTP adapter communicates with a fake local DocFlow endpoint using its versioned contract and reconstructs canonical extraction facts and identifiers.
3. Offline network-free tests cover success and identity parity, version mismatch, validation invalid, malformed/oversized response, HTTP failure, timeout, cancellation, credential/privacy redaction and SSRF rejection.
4. Port and adapter can be composed with DI without starting DocFlow in-process.
5. Exact-HEAD full build/tests, API+Postgres smoke, CodeQL when available, deployment validation and privacy scan pass.
6. Draft PR only; orchestrator review/merge. Do not initiate ARCH-04 before ARCH-03 integration.

## Hard safety rule
ARCH-03 is a read-only document extraction dependency. Do not add order mutation, broker requests, or secret-bearing request fields. OP-03 remains WAITING_EXTERNAL.

## Implementation checkpoint — 2026-10-08

- TradeOps-owned `IDocFlowExtractionPort`, `DocFlowExtractionRequest` and `DocFlowExtractionResult` added in Application, carrying canonical raw/schema JSON and canonical normalized/structured JSON without TradeOps domain contamination.
- Infrastructure HTTP adapter `DocFlowHttpExtractionAdapter` implemented for DocFlow `POST /api/v1/extractions` contract v1; errors sanitized; rejects invalid payload, unsupported provider/version/response structure; preserves complete nested JSON and audit identity; 1 MiB request / 2 MiB response limits; cancellation and timeout supported by `HttpClient`.
- Typed DI registration explicit opt-in through `AddTradeOpsDocFlowHttp`, configuration `DocFlow:BaseAddress`, 30-second timeout and auto-redirect disabled.
- Constructor blocks non-loopback plain HTTP, embedded URI credentials and unsupported URI schemes. No user-supplied URI in extraction request. **Production private network or independently authenticated TLS boundary still required; public unauthenticated DocFlow access is not approved.**
- Offline fake-handler tests: success preserving canonical document/fingerprint/data, version/schema failures, invalid JSON, bounded response, sanitized provider errors, unsupported provider, canceled request, unsafe base URLs. No live network or credentials.
- Draft PR: https://github.com/avysotsky/TradeOps/pull/73. No broker methods, trades, DB or live providers are touched.
- CI: pending latest exact-head validation. Do not merge without full green checks.
- This initial contract port does not introduce a new orchestration endpoint or erase existing CLI child-process regression path; integration at the research-composition layer belongs to the following bounded adapter use case.

## Integration record — 2026-10-08

PR #73 merged to TradeOps main via squash commit `4ead2320733fea459b51fce64de17a07bf6f4246`. Final PR-head `3f81d61b9e956ee8fb735476e767e719465c6abf` passed exact-head GitHub Actions build `37806608859` SUCCESS (compile, unit tests, API/PostgreSQL smoke, signed webhook/TradingView, deployment/Docker). Dynamic PR #73 check `37806604366` SUCCESS. Post-merge push-triggered CI is not yet independently verified. No broker mutation or persistence added. Remaining deployment constraint: no public unauthenticated DocFlow HTTP; use localhost/private/secure service mesh until service authentication exists. ARCH-04 follows using the **same TradeOps-owned port** with a direct in-process extraction adapter.
