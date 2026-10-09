# ARCH-04 ADR — Embedded CPython under TradeOps .NET 8

## Decision
**Feasible at interpreter level; production integration not yet authorized.**

The standalone probe in `tools/TradeOps.DocFlowInProcessProbe` loads CPython 3.11 using Python.NET 3.1.0 inside a .NET 8 process and checks `os.getpid()` against `Environment.ProcessId`. It also checks a Python `asyncio.run` coroutine returns that identical PID. No child process, HTTP loopback or Python code reimplementation is used. GitHub Actions `ARCH04 Python Embed Probe` is the evidence gate.

This verifies **only** embedded interpreter + coroutine mechanics. It does not yet verify actual DocFlow `extract_text_document` implementation, provider selection, Pydantic/native library loading, concurrency or shutdown behavior when hosted long-term under ASP.NET.

## Recommended implementation strategy
1. Keep ARCH-03 versioned HTTP adapter as the supported production integration.
2. Build a restricted, opt-in in-process adapter for `IDocFlowExtractionPort` using the existing DocFlow Python package and its `extract_text_document` entrypoint, not a clone.
3. Package Python 3.11 and DocFlow application as an explicit deployment artifact; two Git repos remain independent. Pin package hash/revision, enforce license review and SBOM.
4. Initialize one interpreter per host lifetime; use a dedicated worker/queue and GIL-aware scheduling. Never hold a GIL over arbitrary .NET blocking I/O. Avoid sharing Python objects across requests.
5. Control request time budgets; Python coroutine cancellation does not by itself guarantee preemptive interruption of native calls. Fail closed; never promise hard cancellation before tested.
6. Pass generic request/schema JSON only. Credential lookup must occur only through DocFlow's provider env configuration. Avoid logging payloads, prompts and provider exceptions.
7. Add deterministic fixture comparison with the real DocFlow Python app before claiming parity with HTTP.
8. Disable embedding by default until actual DocFlow fixture, installation/portability and graceful shutdown tests pass.

## Alternatives rejected for claiming in-process completion
- Localhost HTTP: still networking, two application hosts.
- Child-process JSON or CLI: still a separate process.
- Reimplementing Python normalization or schema-engine behavior in C#: introduces divergent application semantics.

## Risks and blockers
- Interpreter GIL, Python/.NET thread attachment and CPython shutdown correctness.
- Native Python extension libraries and multi-platform Python DLL discovery.
- Python.NET compatibility and package/security/licensing review.
- No supported DocFlow Python package distribution/current installation in TradeOps CI, so the actual DocFlow application cannot yet be exercised by this probe.
- Need explicit repository-to-repository secure source/package consumption with pinned revision, rather than network-dependent CI pulls or embedding credentials.
- Production real-provider request cancellation/timeout/isolation.
- Public unauthenticated DocFlow HTTP exposure remains prohibited independently of embedding.

## Evidence
- [Python.NET embedding documentation](https://pythonnet.github.io/pythonnet/dotnet.html)
- [Python.NET threading/GIL documentation](https://pythonnet.github.io/pythonnet/threading.html)
- [Python.NET 3.1.0 NuGet package](https://www.nuget.org/packages/pythonnet/3.1.0)

## Acceptance status
- Same-process .NET + CPython native embedding: tested in isolated GitHub workflow.
- Python coroutine inside same process: tested in updated isolated GitHub workflow.
- Actual DocFlow Python normalization/schema extraction in-process: **NOT PROVEN**.
- Same TradeOps `IDocFlowExtractionPort` implementation via embedded Python: **NOT IMPLEMENTED**.
- HTTP vs in-process canonical facts equivalence: **NOT PROVEN**.
- Production shipping decision: **DEFER**, retaining ARCH-03 HTTP topology.

No broker, order, risk-mutation or customer records are involved.
