# TO-V2-24 Status

- State: **IMPLEMENTATION IN PROGRESS, Gate A PENDING**. Earlier design-only
  registration was superseded by a new worker implementation commit.
- Frozen base: TO-V2-23 `b586cbafc2b29455708de221dbd5771bb1840b8f`.
  Owner-qualified mainnet read-only baseline: 14 account currency entries,
  2 positions, 0 open orders, 0 mutating requests.
- Implemented: pure `DeribitReadOnlyEvidenceParser` for native Deribit
  position size vs base size; strict future/option/combo kind and
  direction validation; duplicate/precision and missing-data denial.
  Pure trade-page parser preserves native amount, fee currency, IDs
  and timestamp, distinguishes historical query and `has_more` with
  `IsHistoricalCoverageProven=false` (not a full collector).
- Integration: existing isolated non-`IExchangeClient` read-only probe now
  validates every position with pure parser; outputs aggregate
  future/option/combo counts only, no names, balances or real IDs.
  Same three fixed whitelisted HTTPS GETs, no new endpoint.
- Security: no API/Worker DI registration, no state mutation or
  order placement/cancel/withdraw, no WebSocket, no extra credentials,
  no public exposure, no PostgreSQL changes.
- Tests: synthetic xUnit positions and single history page parse,
  invalid state/number, duplicate IDs, incomplete page, raw unit
  preservation, integration-level count-only guard.
- Gate A: **PENDING independent WSL2 exact new SHA relay result**
  (restore, build, zero failed/skipped xUnit, cleanup 6/6).
- Owner Gate B: **NOT RUN for TO-V2-24**. Owner will explicitly opt in
  to rerun read-only three-call probe after qualified Gate A on exact SHA.
- Historical fills: **NOT CONNECTED TO MAINNET YET**; account/time-based
  pagination, session continuity, duplicate across pages and
  historical/live indexing gap need their own bounded stage.
- Next: review independent qualification, freeze exact SHA, then
  provide owner opt-in script and evaluate sanitized counts.
