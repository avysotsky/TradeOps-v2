# ARCH-04 — In-Process DocFlow Adapter (Architecture Feasibility Gate)

## State
FEASIBILITY_PROVEN — same-PID interpreter/coroutine CI PASS; actual DocFlow in-process adapter NOT IMPLEMENTED

## Baselines
- TradeOps main at registration: `54be1190764703b8dd76a47356daecd9bfbe8d49`.
- DocFlow main: `d6de1b5168c156b107cb3c3d71ef29983401ad40`.
- TradeOps-owned port: `IDocFlowExtractionPort`, introduced in ARCH-03, PR #73 merge `4ead2320733fea459b51fce64de17a07bf6f4246`.
- Branch: `TradeOps/arch04-docflow-inprocess-adapter`.
- Canonical plan: `docs/orchestration/DUAL_TOPOLOGY_ARCHITECTURE_PLAN.md`.

## Critical architecture constraint

DocFlow extraction implementation currently runs in **Python** (Pydantic, Python LLM adapter); TradeOps.Api is **C#/.NET 8**. A direct function call across two language runtimes **in one operating-system process** requires embedded Python runtime/interoperability or migration. It cannot be achieved merely by registering an HTTP client in one DI container. A child process is not in-process; localhost HTTP is not in-process; a duplicated C# reimplementation is not reuse of the Python application flow.

ARCH-04 must begin with a **bounded feasibility/ADR gate**: identify supported embedding strategy, deployment/runtime packaging, native dependency complexity, Python GIL/concurrency, process lifetime, cancellation, error sanitization and host shutdown. Do not implement misleading fake in-process semantics.

## Potential option to investigate

- TradeOps-owned C# `IDocFlowExtractionPort` implemented through embedded CPython invoked from C# via a strictly bounded interoperability layer (e.g. pythonnet, subject to independent package/security/licensing/version compatibility review).
- The same existing Python `docflow_worker.text_artifact_pipeline.extract_text_document` must execute within the TradeOps host process, producing the same normalized and structured JSON without HTTP.
- Python provider credentials must remain environment-only; no credential transfer through TradeOps DTOs.
- Embed only when safely deployable; isolate interpreter initialization, GIL access, callbacks, cancellation and provider timeouts.
- Prototype deterministic fake backend, no real LLM credentials or network required.
- Keep the ARCH-03 HTTP adapter as working fallback and regression reference. Do not switch default from HTTP automatically.

## Feasibility acceptance

1. Demonstrate **same PID** executing TradeOps HTTP host and DocFlow Python extraction; no helper process and no loopback HTTP.
2. No reimplementation of normalization or schema extraction in C#; use DocFlow Python production entrypoint.
3. One shared TradeOps application port interface across HTTP and in-process paths; no TradeOps dependency inside DocFlow.
4. Deterministic synthetic fixture parity with DocFlow direct call/HTTP contract, preserving document and segment IDs, fingerprint, structured result and validation status.
5. Validated deployment and package licensing/runtime portability; handle native Python dependencies, interpreter shutdown, threading and GIL.
6. Fail-closed errors, bounded inputs, no logged prompts/secrets, cancellation behavior explicitly documented.
7. Full exact-head CI and tests; no broker/order execution or persistence.
8. If unsupported or high-risk, produce a clear ADR recommending deferral and keep ARCH-03 as the supported topology. **Do not claim equivalence before evidence.**

## First worker action

Fetch live TradeOps and DocFlow GitHub state, compare current branch, read canonical roadmap, ARCH-01 API implementation, ARCH-03 port and this specification. Research feasible in-process interop with current .NET 8 + Python 3.11 and dependency packaging. Record ADR and a runnable synthetic proof, or an explicit feasibility blocker, before production changes.

## Completion

Draft PR only. Supply exact HEAD/CI, repository files, same-process evidence or blocker and decision for ARCH-05. Orchestrator owns merge. No third repository, monorepo or NuGet packaging yet.

## Completed feasibility checkpoint — 2026-10-08

PR #74 squash merged as `52dbfe375f63857e733d5c6617f987960f4338a3`; exact-head `439ec026dfc9cd04e47050e0b64f89da451c5de1`. Python.NET embedded CPython 3.11 and async coroutine same-PID proof passed `ARCH04 Python Embed Probe` run `37807983142` SUCCESS. Full TradeOps build run `37807982919` SUCCESS. The initial async probe error resulted from transient Python scope; corrected to one persistent `Py.CreateScope()`. The minimal isolated POC introduces no production runtime dependency. ADR: `docs/orchestration/ARCH04_EMBEDDED_PYTHON_FEASIBILITY_ADR.md`.

**The actual production-grade in-process DocFlow adapter is not implemented or certified.** The evidence establishes a same-process runtime option, not completed application-level parity. Next engineering gate: pinned/secure DocFlow package distribution, actual `extract_text_document` invocation using deterministic fake backend, GIL/cancellation/native dependency tests, and then ARCH-05 HTTP equivalence. Retain ARCH-03 HTTP adapter as default in the meantime. No broker mutation.
