# TO-V2-02 — .NET 10 migration notes

## Scope and dependencies

Migrate all 15 .NET projects from net8.0 to net10.0 (14 in TradeOps.sln plus tools/MigrationSmoke), use pinned SDK 10.0.401/latestPatch and upgrade Microsoft-owned dependencies to stable 10.0.12 and Npgsql.EntityFrameworkCore.PostgreSQL to 10.0.3. Retain xunit 2.9.3, test SDK 18.10.1, Swashbuckle.AspNetCore 10.2.3, MessagePack 3.1.8 and Nethereum.Signer.EIP712 6.1.0 pending real restore/build compatibility evidence.

## Compatibility and invariants

- EF Core 8 to 10 is a major upgrade: inspect change tracking, translated LINQ, generated SQL, concurrency and provider type mappings through isolated tests before release. No migrations, schema, seed, DbContext model or production DB changes in this PR.
- Npgsql provider and EF major versions are aligned at 10.x, but compatibility is **not proven** until owner runs warning-as-error restore and tests. In particular, review DateTime/timestamptz, JSON and numeric mapping, raw SQL behavior and the query-plan PostgreSQL suites in an isolated disposable database.
- ASP.NET Core 10 upgrades hosting and WebApplicationFactory test infrastructure. HMAC raw-body signing, timestamps, replay checks, API auth, TradingView ingress, serialization and response codes must preserve exact semantics. No intentional endpoint contract or middleware modifications.
- Order placement/cancellation, risk approval and exposure thresholds, idempotency, reconciliation, exchange policies/allowlists, account configuration, provider credentials, portfolio and research policy remain unchanged. Do not run gateway/demo/provider smoke tools as part of the validator.
- Existing HTTP DocFlow adapter remains; neither a new in-process adapter nor DocFlow contracts are modified. DocFlow-v2 source main `3f09e91ba356fb367248ef1b48a56b19d9fdae97` has a separate net10 migration. Private immutable DocFlow NuGet publication and consumer ACL remain blocked; cross-repo authenticated locked restore is NOT RUN.
- Both Dockerfiles use sdk:10.0; API final uses aspnet:10.0, Worker final uses runtime:10.0. Docker compose entries refer to the Dockerfiles, not fixed 8.0 image versions. **Do not run compose up**, API, Worker or a broker-connected smoke executable. Docker builds are NOT RUN.
- global.json requires 10.0.401 stable SDK with latestPatch. Windows 10 + VS 2026 and D:\DotNet\dotnet.exe are the owner target environment. Historical README/documentation references to previous 8.0 CI or archived v1 releases should not be rewritten as current proof.

## Local owner verification

From PowerShell, after synchronizing the assigned branch and ensuring a clean tracked working tree:

```powershell
Set-Location "D:\Projects\TradeOps-v2"
git fetch origin
git switch TradeOps/to-v2-02-dotnet10-migration
git pull --ff-only origin TradeOps/to-v2-02-dotnet10-migration
& .\scripts\to-v2-02-validate-local.ps1
```

The validator performs Git branch and remote SHA checks, validates exactly 15 net10 csproj, SDK and Docker images, restores and builds solution and each project with warning-as-error, then runs a narrow offline unit test allowlist. Inspect fixtures before widening it. PostgreSQL integration/query-plan tests and TestServer tests potentially invoking real persistence remain intentionally excluded from the unattended gate; separate disposable DB review and isolated integration testing are required.

## Acceptance blockers

Owner must supply exact-HEAD offline restore/build results, test passed/failed/skipped counts, all compile warnings, and any EF/Npgsql breaking behavior findings. Current worker environment has not executed dotnet or Docker. Do not merge until orchestrator independently reviews and owner validates. GitHub Actions OFF; no workflow changes, NuGet publication, real trading or production data writes.

## 2026-10-10 — NU1903 security remediation

First owner Windows restore at original head `32fa8a2c3bac6c9508b8ab7d378d31b2ea0a5bf5` failed with 5 × NU1903 on Newtonsoft.Json 11.0.2. Published `Nethereum.Signer.EIP712 6.1.0` dependency metadata allows Newtonsoft.Json `>=11.0.2 && <14`; NuGet chose the vulnerable lower bound. Directly pinning `Newtonsoft.Json 13.0.4` in Infrastructure is compatible with that advertised version range, but owner-local full resolved graph and compile/run compatibility **require revalidation**. Validate all 15 `project.assets.json` through the updated script; do not suppress NU1903 or disable NuGet Audit. Repeat offline signing and serialization regression tests before approval, and retain immutable EIP-712 signature behavior. No provider egress or production DB activity is authorized.
