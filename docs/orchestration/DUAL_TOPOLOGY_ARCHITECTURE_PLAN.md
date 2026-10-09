# Dual-Topology Architecture Plan — DocFlow + TradeOps

## Status

```text
APPROVED_ARCHITECTURAL_DIRECTION
```

Date recorded:

```text
2026-10-08
```

This document records the target architecture agreed for the next major phase after the current VS-17 execution-dry-run slice.

The objective is to support both:

1. independently deployable HTTP services for DocFlow and TradeOps; and
2. an in-process modular monolith that reuses the same DocFlow and TradeOps application code without routing internal calls through HTTP.

The deployment topology must not define or duplicate the application/business architecture.

---

## 1. Architectural objective

Target end state:

```text
                    ┌──────────────────────────┐
                    │       HTTP clients       │
                    └─────────────┬────────────┘
                                  │
              ┌───────────────────┴───────────────────┐
              │                                       │
              ▼                                       ▼
   ┌─────────────────────┐                 ┌─────────────────────┐
   │   DocFlow.Api       │                 │    TradeOps.Api     │
   │   HTTP service      │                 │    HTTP service     │
   └──────────┬──────────┘                 └──────────┬──────────┘
              │                                       │
              ▼                                       ▼
   ┌─────────────────────┐                 ┌─────────────────────┐
   │ DocFlow.Application │                 │ TradeOps.Application│
   │ DocFlow.Domain      │                 │ TradeOps.Domain     │
   └──────────┬──────────┘                 └──────────┬──────────┘
              │                                       │
              └───────────────────┬───────────────────┘
                                  │
                                  ▼
                    ┌──────────────────────────┐
                    │ TradeOps.Monolith.Host   │
                    │                          │
                    │ DocFlow.Application      │
                    │          +               │
                    │ TradeOps.Application     │
                    │ same process / DI        │
                    └──────────────────────────┘
```

The important constraint is:

> HTTP is a delivery/transport boundary, not the business-logic boundary.

The same application logic must be callable:

```text
in-process
```

or:

```text
over HTTP
```

depending only on the composition root / deployment topology.

---

## 2. Desired deployment modes

### 2.1 DocFlow as an independent HTTP service

Example external surface:

```text
POST /api/v1/documents/extract
POST /api/v1/documents/normalize
POST /api/v1/extractions
GET  /api/v1/jobs/{id}
```

The exact API is not frozen yet.

The internal structure should be:

```text
HTTP
 ↓
DocFlow.Api
 ↓
DocFlow.Application
 ↓
DocFlow domain/application abstractions
 ↓
provider/infrastructure adapters
```

Controllers/endpoints must not contain extraction business logic.

A representative application abstraction may look conceptually like:

```csharp
public interface IStructuredDocumentExtractor
{
    Task<StructuredExtractionResult> ExtractAsync(
        TextArtifact artifact,
        ExtractionSchema schema,
        CancellationToken cancellationToken = default);
}
```

The implementation must not care whether it was invoked from HTTP, CLI, worker, test, or in-process monolith composition.

### 2.2 TradeOps as an independent HTTP service

Candidate HTTP surface:

```text
POST /api/v1/research/decisions
POST /api/v1/rebalance/preview
POST /api/v1/risk/preview
POST /api/v1/execution/dry-run

GET  /api/v1/orders
GET  /api/v1/positions
GET  /api/v1/risk/status
```

Later, only after broker gates are passed:

```text
POST /api/v1/execution/submit
```

The internal structure should be:

```text
TradeOps.Api
 ↓
TradeOps.Application
 ↓
Research / Backtest / Rebalance / Risk / Execution
```

Controllers/endpoints should orchestrate application services but not contain strategy, risk, portfolio, execution, or broker logic.

### 2.3 Modular monolith deployment

The monolith must not call its own HTTP endpoints.

Do not implement:

```text
TradeOps
→ HTTP localhost
→ DocFlow
```

for the in-process topology.

Instead:

```text
TradeOps application code
        ↓
DocFlow application abstraction
        ↓
DocFlow.Application
```

inside the same process and dependency-injection container.

Conceptually:

```csharp
var extraction =
    await docFlowExtractor.ExtractAsync(...);

var decision =
    await researchService.CreateDecisionAsync(extraction, ...);

var plan =
    await rebalanceService.PlanAsync(decision, ...);

var risk =
    await riskEngine.CheckAsync(...);
```

The monolith is therefore a composition host, not a separate implementation of business logic.

---

## 3. Bounded-context ownership

Keep DocFlow and TradeOps as separate bounded contexts.

### DocFlow owns

```text
documents
→ normalization
→ segmentation
→ schema-driven extraction
→ structured facts
```

DocFlow must not know about:

```text
portfolio
positions
broker
risk
orders
backtests
execution lifecycle
```

### TradeOps owns

```text
ResearchDecision
→ portfolio target
→ backtest
→ rebalance
→ risk
→ execution
→ broker
```

TradeOps must not own:

```text
PDF parsing
OCR
LLM document normalization
generic document segmentation
generic schema extraction internals
```

The existing separation between repositories remains desirable.

---

## 4. Repository strategy

For now keep:

```text
avysotsky/DocFlow
avysotsky/TradeOps
```

Do not merge them into one repository merely to support monolith deployment.

Do not create a third repository yet.

A future composition repository or host may eventually be justified, for example:

```text
avysotsky/TradeOpsPlatform
```

with:

```text
src/
  TradeOpsPlatform.Host
```

but only after both existing projects expose clean reusable application boundaries.

The third host/repository is a later composition concern, not the immediate next step.

---

## 5. Cross-context integration rule

TradeOps should not directly depend throughout its application layer on concrete DocFlow implementation types.

Use a TradeOps-owned port/abstraction.

Representative concept:

```csharp
public interface IEarningsDocumentExtractor
{
    Task<EarningsFacts> ExtractAsync(
        ...,
        CancellationToken cancellationToken = default);
}
```

Then provide topology-specific adapters.

### Distributed topology

```text
TradeOps.Application
      ↓
IEarningsDocumentExtractor
      ↓
HttpDocFlowEarningsExtractor
      ↓ HTTP
DocFlow.Api
```

### In-process topology

```text
TradeOps.Application
      ↓
IEarningsDocumentExtractor
      ↓
InProcessDocFlowEarningsExtractor
      ↓
DocFlow.Application
```

TradeOps application behavior must remain unchanged between the two topologies.

The same principle may be generalized into a broader document-research gateway if required by actual use cases.

Example concept:

```csharp
public interface IDocumentResearchGateway
{
    Task<StructuredResearchInput> ProcessAsync(
        ResearchDocument document,
        CancellationToken cancellationToken = default);
}
```

Do not prematurely generalize beyond demonstrated needs.

---

## 6. Contracts packages

Introduce small, bounded contracts layers when needed.

Potential DocFlow package:

```text
DocFlow.Contracts
```

Candidate contents:

```text
TextDocumentRequest
ExtractionSchemaRequest
StructuredExtractionResult
ExtractionValidationResult
```

Potential TradeOps package:

```text
TradeOps.Contracts
```

Candidate contents:

```text
ResearchDecision request/response transport contracts
RebalancePreview request/response
RiskPreview response
ExecutionDryRun response
```

Rules:

- each bounded context owns its own contracts;
- do not create a catch-all `Common.Shared.Core.Utilities` package;
- transport DTOs must not become a second competing domain model;
- application/domain contracts remain canonical where already frozen;
- only publish contracts needed by external hosts/adapters.

---

## 7. Packaging and composition strategy

Long-term preferred distribution for reusable modules:

```text
NuGet packages
```

rather than git submodules.

Possible DocFlow packages:

```text
DocFlow.Contracts
DocFlow.Application
DocFlow.Infrastructure
```

Possible TradeOps packages:

```text
TradeOps.Contracts
TradeOps.Application
TradeOps.Infrastructure
```

A future monolith composition host can consume these packages.

For local development, project references / a local aggregate solution may be used first.

Package publishing infrastructure should be introduced only when the reusable boundaries are stable enough to justify it.

---

## 8. Target solution structure

### DocFlow

Target conceptual structure:

```text
DocFlow.sln

src/
  DocFlow.Domain
  DocFlow.Application
  DocFlow.Contracts
  DocFlow.Infrastructure

hosts/
  DocFlow.Api
  DocFlow.Worker

tests/
  DocFlow.UnitTests
  DocFlow.IntegrationTests
```

Current DocFlow layout may be migrated incrementally; do not perform a large-bang directory rewrite without need.

### TradeOps

Target conceptual structure:

```text
TradeOps.sln

src/
  TradeOps.Domain
  TradeOps.Application
  TradeOps.Contracts
  TradeOps.Infrastructure

hosts/
  TradeOps.Api
  TradeOps.Worker

tools/
  ...

tests/
  TradeOps.UnitTests
  TradeOps.IntegrationTests
```

Existing `TradeOps.Api` and `TradeOps.Worker` should be evolved toward this architecture rather than recreated unnecessarily.

---

## 9. Commercial/deployment model

The architecture should allow multiple deployment shapes from the same application code.

### Small deployment

```text
single container
single process
DocFlow + TradeOps in-process
```

### Distributed deployment

```text
DocFlow service
TradeOps service
independent deployment/scaling
HTTP between bounded contexts
```

Conceptual runtime examples:

```text
docker run docflow
:8081

docker run tradeops
:8082
```

and later:

```text
docker run tradeops-monolith
:8080
```

The commercial positioning is:

```text
Small deployment:
single process / low operational overhead

Scaled deployment:
DocFlow and TradeOps independently deployable services
```

No business logic should need to be rewritten when switching deployment topology.

---

## 10. Current transitional state

Today, part of the cross-repository demo flow uses:

```text
TradeOps tool
→ child process
→ DocFlow CLI
→ JSON artifacts
→ TradeOps continuation
```

This is acceptable as a deterministic validation harness.

It is not the final production integration architecture.

Do not remove the existing harness until equivalent HTTP and in-process paths are implemented and tested.

The harness remains useful for regression and provider E2E validation.

---

## 11. Architecture roadmap

The next architecture phase begins after the currently open VS-17 slice.

### ARCH-01 — DocFlow Application Boundary → HTTP Host

Goal:

- expose the already proven DocFlow extraction application flow through a clean HTTP host;
- move HTTP-specific behavior to the edge;
- preserve existing CLI/provider behavior;
- avoid TradeOps dependencies.

Acceptance characteristics:

- HTTP request invokes the same application code used by non-HTTP paths;
- request/response DTOs are versioned and bounded;
- provider credentials remain server-side/environment-only;
- errors are bounded/sanitized;
- health/readiness endpoint exists;
- deterministic fixture-based integration tests;
- no TradeOps-specific semantics in DocFlow.

### ARCH-02 — TradeOps Research/Risk Pipeline → HTTP Host

Goal:

Expose the existing deterministic pipeline via HTTP without moving business logic into controllers.

Initial candidate endpoints:

```text
POST /api/v1/research/decisions
POST /api/v1/rebalance/preview
POST /api/v1/risk/preview
POST /api/v1/execution/dry-run
```

No live broker mutation endpoint until independent broker gates pass.

Acceptance characteristics:

- existing application services reused;
- existing VS-16/VS-17 semantics preserved;
- HTTP DTOs do not replace domain/application models;
- no provider/document-processing logic moved into TradeOps.

### ARCH-03 — DocFlow HTTP Client Adapter for TradeOps

Goal:

Provide a distributed implementation of the TradeOps-owned DocFlow port.

Shape:

```text
TradeOps
→ TradeOps-owned abstraction
→ HttpDocFlow adapter
→ DocFlow.Api
```

Requirements:

- typed HttpClient or equivalent;
- strict request/response versioning;
- explicit timeouts/cancellation;
- bounded retries only where safe;
- no provider credential passed through TradeOps unless contract explicitly requires it;
- clear transient/permanent error classification;
- correlation/provenance preservation.

### ARCH-04 — In-Process DocFlow Adapter for TradeOps

Goal:

Provide a second implementation of the same TradeOps-owned port that directly calls DocFlow application services.

Shape:

```text
TradeOps
→ same TradeOps-owned abstraction
→ InProcessDocFlow adapter
→ DocFlow.Application
```

Requirements:

- no HTTP loopback;
- same semantic result as ARCH-03;
- same application contract from TradeOps perspective;
- one process / one DI composition root;
- no duplicate business logic.

### ARCH-05 — Topology Equivalence Harness

Required acceptance proof:

```text
same input
→ HTTP topology
→ result X

same input
→ in-process topology
→ result Y

X and Y must be semantically equivalent
```

Compare at least:

- normalized/structured research facts;
- ResearchDecision;
- RebalancePlan;
- RebalanceOrderIntent;
- risk result;
- execution dry-run result;
- provenance/correlation identifiers where contractually stable.

This is the key proof that there is one application architecture with two deployment topologies.

---

## 12. Non-goals

Do not:

- build microservices merely for architectural fashion;
- force HTTP between modules in the monolith;
- duplicate DocFlow extraction logic inside TradeOps;
- duplicate TradeOps business logic inside DocFlow;
- create generic shared-core dumping grounds;
- introduce distributed messaging before a demonstrated requirement;
- introduce Kubernetes merely to validate the architecture;
- rewrite the repositories into a monorepo;
- remove existing CLI/demo harnesses before replacement paths are proven;
- enable IBKR mutation as part of this architecture work;
- couple this plan to availability of an IBKR Paper account.

---

## 13. Guiding principles

1. One application architecture, multiple deployment topologies.
2. HTTP is an adapter/host concern.
3. Bounded contexts remain independent.
4. TradeOps owns the interface it needs from document processing.
5. DocFlow owns document-processing semantics.
6. In-process composition does not use loopback HTTP.
7. Distributed composition uses explicit versioned HTTP contracts.
8. Existing deterministic application services are reused, not reimplemented.
9. Migration is incremental and test-driven.
10. The current validated CLI/child-process path remains until replacements have parity.
11. Broker mutation remains independently gated.
12. Topology equivalence must be demonstrable by automated tests.

---

## 14. Near-term execution order

Current order:

```text
VS-17
Risk-approved intent → execution dry-run/audit boundary
        ↓
ARCH-01
DocFlow reusable application boundary + HTTP host
        ↓
ARCH-02
TradeOps research/rebalance/risk/dry-run HTTP host
        ↓
ARCH-03
TradeOps → DocFlow HTTP adapter
        ↓
ARCH-04
TradeOps → DocFlow in-process adapter
        ↓
ARCH-05
HTTP topology ↔ monolith topology equivalence harness
```

OP-03 / real IBKR Paper validation stays:

```text
WAITING_EXTERNAL
```

and may be resumed independently when a real IBKR Paper account/session becomes available.

---

## 15. Definition of architectural success

The architecture phase is successful when all of the following are true:

- DocFlow can run as a standalone HTTP service;
- TradeOps can run as a standalone HTTP service;
- TradeOps can consume DocFlow over HTTP;
- TradeOps can consume DocFlow in-process through the same TradeOps-owned port;
- a single-process host can compose both application layers;
- business logic is not duplicated between HTTP and monolith paths;
- deterministic topology-equivalence tests pass;
- current provider-backed and research/rebalance/risk/dry-run behavior remains intact;
- broker mutation remains separately controlled by broker-specific validation gates.

Final architectural statement:

> Build DocFlow and TradeOps as reusable bounded application modules with independent HTTP hosts, while preserving the ability to compose both modules in one process through DI and in-process adapters. Deployment topology must be switchable without rewriting business logic.
