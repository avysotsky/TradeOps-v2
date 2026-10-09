# TradeOps v2 — Active Engineering Roadmap (PRIVATE)

## Migration baseline
- Public v1 source snapshot (read-only reference): `avysotsky/TradeOps@77229d7f4f835b8cf43eb0aeae427d2df509bb11`.
- Private development target: `avysotsky/TradeOps-v2`; clean Git history, **not a fork**.
- 545 source-tracked files copied byte-for-byte from pinned v1 SHA. 34 historical documentation/obsolete Python embedding probe files intentionally omitted. Added private migration metadata.
- Source-level audit: no missing or changed retained blobs; Git tree blob SHAs match exactly.
- Private full build, unit tests, API/PostgreSQL smoke and Docker/deployment checks: [GitHub Actions 37910174501](https://github.com/avysotsky/TradeOps-v2/actions/runs/37910174501) **SUCCESS** (code snapshot, prior to this documentation commit).
- Public v1 was archived only after private build/test and source-equivalent cross-repo HTTP interoperability succeeded. Both original public URLs and checked deep links remain intact.

## Architecture decisions
- Prefer native **C#/.NET**. Use Python only for quality/functionality that demonstrably exceeds supported .NET alternatives.
- Keep TradeOps and DocFlow separate repos and independent HTTP hosts. The eventual modular-monolith path must reuse .NET application code, not duplicate algorithms.
- TradeOps owns `IDocFlowExtractionPort`; current infrastructure includes a read-only, bounded DocFlow HTTP adapter.
- DocFlow v2 normalized JSON / schema extraction is still in migration; avoid unproven production topology equivalence.
- ResearchDecision → RebalancePlan → RiskPreview → ExecutionDryRun is **non-mutating** for caller-provided snapshots. No bypass around live RiskEngine to execute orders.
- No live trading, no broker order placement, no embedded secrets, no public unauthenticated DocFlow server.

## Planned workstreams
1. **MIG-01**: COMPLETE — private fresh-history code import, integrity checks and CI.
2. **MIG-02**: COMPLETE — private source-equivalent dual-service HTTP smoke `37912246018` SUCCESS; public v1 READMEs/evidence frozen and GitHub Public+Archived confirmed.
3. **DF-V2-01**: COMPLETED in private DocFlow-v2 PR #2 — 58/58 normalized identity parity tests, strict ingress and UTF-8 hashes; still not full API parity.
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
