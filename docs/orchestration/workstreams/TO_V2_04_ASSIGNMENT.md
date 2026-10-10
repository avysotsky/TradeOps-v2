# TO-V2-04 — Current-Main Dual-HTTP Regression Qualification

**State:** Assignment only. Worker not started. Draft PR. Development Orchestrator alone may merge.

## Source baselines

- PRIVATE source repository: `avysotsky/TradeOps-v2`.
- Existing branch: `TradeOps/to-v2-04-current-main-http-qualification` (do not create a replacement).
- Approved TradeOps main: `7fab8a4e862c7fbdc2d717941a189308cd69fc18` (TO-V2-03 merged).
- Approved DocFlow main: `22741d73a67112eae929d65337818b9b53629c8e` (DF-V2-08 and DF-V2-07 merged).
- Owner local checkouts: `D:\Projects\TradeOps-v2`, `D:\Projects\DocFlow-v2`; SDK `10.0.401` at `D:\DotNet\dotnet.exe`.
- Previous full dual-HTTP Windows proof: TradeOps tested SHA `84fa7eaa080800b74f28cde50d1659a3eb3fbab0` against older DocFlow main `7e9dc58947797251ab3be188411f514a6b078ac3`: **8/8 cases, 171 assertions, exit 0**. This proof is valid only for the tested source pair.

## Objective

Deliver a durable, owner-operated, exact-source dual-service HTTP integration regression gate for the CURRENT two main baselines, without altering production code or invalidating historical TO-V2-03 evidence.

## Work

1. Check both current GitHub main refs, existing workstream branch, compare, open PRs and the read-only Actions history. Stop and notify orchestrator if expected refs changed.
2. Review the existing native source-linked `tools/DualHttpContract/Program.cs`, `scripts/to-v2-03-validate-local.ps1`, and TO-V2-03 evidence. Preserve all eight genuine TestServer cases and use the real TradeOps adapter and native DocFlow.Api/facade, not endpoint mocks.
3. Add `scripts/to-v2-04-validate-local.ps1`. It must validate the exact reviewed TO worker HEAD, its origin tracking ref, expected branch, reviewed TO main SHA, read-only clean DocFlow main SHA, SDK10.0.401, all 15 TradeOps net10 projects, restore/build with warnings as errors, and genuine eight-case test output. Fail on any source mismatch or nonzero exit.
4. Favor narrowly refactoring the TO-V2-03 validator into backward-compatible explicit expected-branch/main/dependency pin parameters, with the ORIGINAL historical defaults unchanged. The TO-V2-04 wrapper passes the new verified refs. Do not weaken any Git cleanliness or exact SHA check, or duplicate the whole testing engine.
5. Leave `tools/DualHttpContract/Program.cs`, production `src/`, existing projects, authentication code, and DocFlow-v2 unmodified unless an actual reproducible defect is proven. Keep each HTTP test host's isolated early-loaded JSON configuration and the signed but unapproved client ID negative assertion requiring HTTP 403 before backend dispatch.
6. Document the new current-main acceptance in `docs/orchestration/workstreams/TO_V2_04_STATUS.md` and `TO_V2_04_VALIDATION_NOTES.md`. Distinguish earlier 8/8 proof from new, independent owner execution. Do not fabricate test outcomes.
7. Update only the EXISTING Draft PR for this workstream. Report latest HEAD, exact changed files and commands for owner Windows validation. If no SDK execution is available, state NOT RUN explicitly. Worker does not merge.

## Acceptance and boundaries

- Owner runs new exact-HEAD validator with clean TradeOps worker branch and DocFlow main, confirms .NET SDK 10.0.401, real restore/build and all eight cases with actual assertion count and exit zero.
- Include both approved JWT client 200 and non-allowlisted correctly signed client 403 (before backend invocation), UTF-8/parity, invalid data, request/response bounds, backend faults, cancellation/deadline and concurrency.
- No GitHub Actions runs or workflow changes, no hosted CI. Both repositories PRIVATE.
- No private NuGet release, package publication, registry permissions, real brokers, production DB, external OIDC/LLM, public HTTP listeners or live deployment.
- DF-V2-07 staged archives remain historical candidates under source SHA `927eddee08d084e7759eef1b0a67404711d2b3b1`. Publication/license approval remains blocked. Do not relabel, edit or publish the archives.
- Do not start the in-process DocFlow NuGet adapter: private release and consumer access have not been approved.
