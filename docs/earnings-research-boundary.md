# Earnings research boundary

WS-02 defines the v1 integration boundary between an external earnings/filings research service and TradeOps.

## Boundary

```text
SEC EDGAR filing metadata + structured XBRL facts
-> external acquisition service
-> SecStructuredFiling
-> deterministic accession/period normalization
-> SecFilingEarningsFacts
-> EarningsEvent v1 / EarningsSnapshot v1
-> deterministic earnings assessment
-> explicit target-weight policy
-> ResearchDecision v1 (SetTargetWeight)
-> PortfolioRebalancePlanner
-> RebalancePlan
-> execution/risk boundary
```

TradeOps does not crawl SEC, parse arbitrary HTML/transcripts, run an LLM over documents, persist a raw research corpus, or place orders directly from raw filing text.

## EarningsEvent / EarningsSnapshot v1

`EarningsEvent` is the consumer-side event contract usable by live research and historical replay.

It carries:

- broker-neutral `InstrumentReference`;
- stable `EventId`;
- consumer-eligibility `PublishedAt`;
- fiscal period;
- normalized earnings snapshot;
- source provenance.

The snapshot contains revenue, diluted EPS, net income, gross margin, operating margin and optional guidance direction/ranges.

For SEC v1:

```text
Provider = SEC
SourceDocumentId = accession number
IssuerId = CIK:<CIK>
```

## SEC normalization and historical correctness

The external acquisition service supplies filing metadata and structured facts. TradeOps deterministically selects only facts matching:

- `us-gaap`;
- exact accession;
- exact form;
- exact fiscal-period interval;
- expected unit;
- consolidated/non-dimensional context.

Facts from a later accession are not eligible for an earlier event. Ambiguous exact facts fail closed.

## SEC acceptance vs PublishedAt

`AcceptedAt` is retained as the provider/source timestamp. It is not automatically treated as exact market availability.

The v1 rule is conservative:

```text
if verified PubliclyAvailableAt exists:
    PublishedAt = PubliclyAvailableAt
else:
    PublishedAt = first observed RetrievedAt
```

Required invariants:

```text
SourceTimestamp <= PublishedAt <= RetrievedAt
ResearchDecision.GeneratedAt >= PublishedAt
```

An after-hours event therefore cannot be consumed before its actual/evidence-backed availability boundary. Market-session eligibility is handled later by the planner/backtester/execution path.

## Deterministic earnings assessment

`DeterministicEarningsDecisionRule.Assess` stays in the research layer.

It compares a current event with a prior comparable event using configurable thresholds for:

- revenue growth;
- diluted-EPS growth;
- operating-margin delta.

The result is:

```text
EarningsAssessment.Positive
EarningsAssessment.Neutral
EarningsAssessment.Negative
```

with score, comparable-signal count, KPI deltas and confidence.

This assessment is not itself an executable trading instruction.

## Explicit target-weight policy

The portfolio-facing mapping is supplied explicitly through:

```text
EarningsTargetWeightPolicy(
    PositiveTargetWeight,
    NeutralTargetWeight,
    NegativeTargetWeight)
```

The rule does not contain hidden position sizing.

For long-only WS-03 compatibility each configured target must satisfy:

```text
0 <= TargetWeight <= 1
```

A demo may choose, for example:

```text
Positive -> 0.04
Neutral  -> 0.02
Negative -> 0.00
```

but those values are caller configuration, not an alpha claim or a production recommendation.

## ResearchDecision v1 output

After assessment and policy mapping, the final TradeOps decision is always:

```text
Action = ResearchDecisionAction.SetTargetWeight
TargetWeight = weight selected by the explicit policy
```

The frozen `ResearchDecision v1` is validated before return.

The deterministic DecisionId incorporates:

- event identity;
- strategy identity;
- research thresholds;
- target-weight policy.

Changing the research rule or portfolio mapping therefore changes decision identity rather than silently reusing the same ID.

## WS-03 compatibility

The integrated downstream path is:

```text
ResearchDecision(SetTargetWeight)
-> PortfolioRebalancePlanner
-> RebalancePlan
-> RebalanceOrderIntent
```

WS-02 tests execute that path directly and require a non-blocked `RebalancePlan` for the positive demo scenario.

`RebalanceOrderIntent` remains pre-risk and is not a direct broker order.

## LLM boundary

A future LLM may extract or classify source facts, but it must not directly create executable BUY/SELL orders.

The required shape remains:

```text
raw filing/transcript
-> normalized facts
-> deterministic assessment
-> explicit policy
-> ResearchDecision(SetTargetWeight)
-> portfolio/risk/execution
```

## Separate repository decision

Live SEC acquisition, polling, HTTP policy/rate limiting, caching, filing discovery, raw payload evidence and future transcript/LLM extraction belong in a separate Earnings Research service/repository.

TradeOps owns the consumer-side contract, deterministic normalization, availability/provenance guards and translation into frozen `ResearchDecision v1`.
