# TO-V2-02 owner-local offline validation; no API/Worker startup, Docker Compose or broker smoke.
$ErrorActionPreference = 'Stop'
Set-Location "D:\Projects\TradeOps-v2"
$dotnet = "D:\DotNet\dotnet.exe"
if (-not (Test-Path -LiteralPath $dotnet)) { throw "Missing SDK executable: $dotnet" }
$env:DOTNET_ROOT = "D:\DotNet"
$env:PATH = "D:\DotNet;" + $env:PATH
$expectedBranch = 'TradeOps/to-v2-02-dotnet10-migration'
function Run-Checked([string]$description, [scriptblock]$operation) {
    Write-Host "CHECK: $description"
    & $operation
    if ($LASTEXITCODE -ne 0) { throw "$description failed (exit $LASTEXITCODE)" }
}
Run-Checked 'git fetch origin' { git fetch origin }
$branch = (git branch --show-current).Trim()
if ($branch -ne $expectedBranch) { throw "Wrong branch: $branch" }
if ((git status --porcelain) -ne $null) { throw 'Working tree is not clean' }
$head = (git rev-parse HEAD).Trim()
$remote = (git rev-parse "origin/$expectedBranch").Trim()
if ($head -ne $remote) { throw "Local HEAD $head differs from remote $remote" }
Write-Host "Exact validated source HEAD: $head"
$sdk = (& $dotnet --version).Trim()
if ($LASTEXITCODE -ne 0) { throw 'dotnet --version failed' }
if ($sdk -ne '10.0.401') { throw "Expected SDK 10.0.401; found $sdk" }
$json = Get-Content -Raw -Encoding UTF8 global.json | ConvertFrom-Json
if ($json.sdk.version -ne '10.0.401' -or $json.sdk.rollForward -ne 'latestPatch' -or $json.sdk.allowPrerelease -ne $false) { throw 'global.json SDK policy mismatch' }
$projects = @(Get-ChildItem src,tests,tools -Recurse -File -Filter '*.csproj' | Sort-Object FullName)
if ($projects.Count -ne 15) { throw "Expected exactly 15 csproj; found $($projects.Count)" }
foreach ($project in $projects) {
    [xml]$xml = Get-Content -Raw -Encoding UTF8 $project.FullName
    $tfm = @($xml.Project.PropertyGroup | ForEach-Object { $_.TargetFramework } | Where-Object { $_ })
    if ($tfm.Count -ne 1 -or $tfm[0] -ne 'net10.0') { throw "Invalid target for $($project.FullName): $tfm" }
}
$migrationSmoke = 'tools/MigrationSmoke/TradeOps.MigrationSmoke.csproj'
if (-not (Test-Path $migrationSmoke)) { throw 'MigrationSmoke missing' }
$solution = Get-Content -Raw TradeOps.sln
if ($solution -match 'TradeOps.MigrationSmoke.csproj') { throw 'MigrationSmoke unexpectedly included in solution; verify project inventory' }
foreach ($file in @('src/TradeOps.Api/Dockerfile','src/TradeOps.Worker/Dockerfile')) {
    $content = Get-Content -Raw $file
    if ($content -notmatch 'mcr.microsoft.com/dotnet/sdk:10.0' -or $content -match 'mcr.microsoft.com/dotnet/(sdk|runtime|aspnet):8.0') { throw "Docker version mismatch: $file" }
}
if ((Get-Content -Raw src/TradeOps.Api/Dockerfile) -notmatch 'mcr.microsoft.com/dotnet/aspnet:10.0') { throw 'API runtime mismatch' }
if ((Get-Content -Raw src/TradeOps.Worker/Dockerfile) -notmatch 'mcr.microsoft.com/dotnet/runtime:10.0') { throw 'Worker runtime mismatch' }
Run-Checked 'Solution restore with warnings as errors' { & $dotnet restore TradeOps.sln -warnaserror }
Run-Checked 'Solution Release build with warnings as errors' { & $dotnet build TradeOps.sln -c Release --no-restore -warnaserror }
foreach ($project in $projects) {
    Run-Checked "Restore $($project.FullName)" { & $dotnet restore $project.FullName -warnaserror }
    Run-Checked "Release build $($project.FullName)" { & $dotnet build $project.FullName -c Release --no-restore -warnaserror }
}
# Explicit allow-list: in-memory deterministic tests, no PostgreSQL integration or provider smoke.
# Do not extend filter without reviewing each test for network, credential and DB side effects.
$safeTestFilter = 'FullyQualifiedName~RiskControlServiceTests|FullyQualifiedName~NonMutatingRiskPreviewTests|FullyQualifiedName~OrderCancellationServiceTests|FullyQualifiedName~OrderBulkCancellationServiceTests|FullyQualifiedName~OrderManagerClientOrderIdentityTests|FullyQualifiedName~OrderRecoveryTests|FullyQualifiedName~ClientOrderIdGeneratorTests|FullyQualifiedName~PositionReconciliationTests|FullyQualifiedName~DocFlowHttpExtractionAdapterTests|FullyQualifiedName~ResearchDecisionValidatorTests|FullyQualifiedName~TradingSignalRequestValidatorTests|FullyQualifiedName~EarningsResearchPolicyConfigurationTests'
Write-Host 'SAFE TEST ALLOW-LIST (review fixture isolation before extending)'
Run-Checked 'Selected offline xUnit tests' { & $dotnet test tests/TradeOps.UnitTests/TradeOps.UnitTests.csproj -c Release --no-build --filter $safeTestFilter --logger 'console;verbosity=normal' }
Write-Host "VALIDATION COMPLETED for $head. Record passed/failed/skipped totals from the console output."
