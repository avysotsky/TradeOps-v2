# TradeOps v2 — Active Engineering Roadmap (PRIVATE)

## Migration baseline
- Public v1 source snapshot (read-only reference): `avysotsky/TradeOps@77229d7f4f835b8cf43eb0aeae427d2df509bb11`.
- Private development target: `avysotsky/TradeOps-v2`; clean Git history, **not a fork**.
- 545 source-tracked files copied byte-for-byte from pinned v1 SHA. 34 historical documentation/obsolete Python embedding probe files intentionally omitted. Added private migration metadata.
- Source-level audit: no missing or changed retained blobs; Git tree blob SHAs match exactly.
- Private full build, unit tests, API/PostgreSQL smoke and Docker/deployment checks: [GitHub Actions 37910174501](https://github.com/avysotsky/TradeOps-v2/actions/runs/37910174501) **SUCCESS** (code snapshot, prior to this documentation commit).
- Public v1 repo must **not** be archived until both v2 repos and cross-repository composition have been independently accepted; v1 URLs are to stay intact.

## Architecture decisions
- Prefer native **C#/.NET**. Use Python only for quality/functionality that demonstrably exceeds supported .NET alternatives.
- Keep TradeOps and DocFlow separate repos and independent HTTP hosts. The eventual modular-monolith path must reuse .NET application code, not duplicate algorithms.
- TradeOps owns `IDocFlowExtractionPort`; current infrastructure includes a read-only, bounded DocFlow HTTP adapter.
- DocFlow v2 normalized JSON / schema extraction is still in migration; avoid unproven production topology equivalence.
- ResearchDecision → RebalancePlan → RiskPreview → ExecutionDryRun is **non-mutating** for caller-provided snapshots. No bypass around live RiskEngine to execute orders.
- No live trading, no broker order placement, no embedded secrets, no public unauthenticated DocFlow server.

## Planned workstreams
1. **MIG-01**: finish fresh-history private code migration, verify both CI suites and snapshot integrity (complete except v1 public archive).
2. **MIG-02**: independent TradeOps ↔ DocFlow v2 smoke and operator/access/privacy review; tag public v1 source snapshots, freeze public READMEs, archive publicly only after evidence.
3. **DF-V2-01**: extend native .NET normalization parity and strict schema validation in DocFlow-v2.
4. **DF-V2-02**: .NET schema-driven extraction / provider adapters retaining Python reference tests.
5. **DF-V2-03**: `DocFlow.Api` thin ASP.NET Core host + safe service auth and versioned HTTP contracts.
6. **TO-V2-01**: share native DocFlow .NET application code via versioned dependency/DI and implement TradeOps in-process port.
7. **ARCH-05**: assert HTTP vs in-process equivalence only with deterministic fixtures and real application adapters.
8. **OP-03**: IBKR Paper integration remains gated on external paper-account credentials and operator approval.

## Orchestration
- GitHub is source of truth; preserve v2 private repository visibility.
- Isolate new work in feature branches, run full CI and review exact HEAD before merge.
- Retain publicly published v1 links. Never push v2 work, tests, features or sensitive architecture back to the public v1 repository.
- Historical workstream documents and PR records remain available in the public v1 history for reference; do not blindly restart finished work.
