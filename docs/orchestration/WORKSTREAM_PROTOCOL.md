# Parallel Workstream Protocol

## Rule 1 — GitHub is the memory bus

Worker chats do not depend on another chat's conversation history.

They coordinate through:

```text
branch HEAD
commits
CI
shared contracts
docs/orchestration/WS*.md
```

## Rule 2 — one branch, one owner

A worker chat writes only to its assigned branch unless the orchestrator explicitly changes ownership.

Do not let two worker chats implement on the same branch.

## Rule 3 — shared contract changes are gated

Shared models used by more than one workstream require an orchestrator decision before modification.

Current frozen shared contract:

```text
InstrumentReference v1
ResearchDecision v1
ResearchDecisionAction v1
ResearchDecisionValidationResult v1
```

## Rule 4 — keep commits integrable

Prefer one meaningful bounded slice:

```text
implementation
-> tests
-> CI
-> status-file update
-> stop
```

Do not stack unrelated milestones in one worker turn.

## Rule 5 — no false capability claims

A worker must distinguish:

```text
implemented
locally tested
CI tested
paper/sandbox tested
live tested
```

IBKR first milestone is Paper only.

Research/earnings work does not imply profitable signals.

Backtest results do not imply future profitability.

## Rule 6 — status file is mandatory

Before worker handoff, update its WS file with:

- exact HEAD;
- exact CI run;
- completed slice;
- any shared contract change;
- blockers;
- next step.

## Rule 7 — integration is orchestrated

Worker chats do not independently merge broad cross-workstream changes into main.

The orchestrator checks:

- CI;
- changed-file overlap;
- dependency readiness;
- public-contract compatibility;
- integration order.

## Rule 8 — stale branch detection

If main advances in an area the worker also changes, the orchestrator must compare before integration and decide whether to rebase/merge main or adapt the worker.

## Rule 9 — bounded context

TradeOps remains execution/risk/portfolio infrastructure.

Earnings/transcript ingestion remains a research bounded context and communicates through structured contracts.

DocFlow remains a separate product; patterns may be reused, but DocFlow code is not silently repurposed as the trading research service.
