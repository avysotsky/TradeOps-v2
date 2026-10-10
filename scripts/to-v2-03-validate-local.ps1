# TO-V2-03: source-linked OFFLINE TestServer gate. Does not start TradeOps Api/Worker,
# bind sockets, contact providers, authenticate to real OIDC, or connect to databases.
# Usage: & .\scripts\to-v2-03-validate-local.ps1 -ExpectedTradeOpsHead '<PR #5 HEAD>'
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $ExpectedTradeOpsHead,
    [string] $TradeOpsRoot = 'D:\Projects\TradeOps-v2',
    [string] $DocFlowRoot = 'D:\Projects\DocFlow-v2',
    [string] $DotNetExe = 'D:\DotNet\dotnet.exe',
    # Original TO-V2-03 defaults are intentional; TO-V2-04 passes explicit refs.
    [string] $ExpectedTradeOpsBranch = 'TradeOps/to-v2-03-dotnet10-dual-http-contract',
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $ExpectedTradeOpsMain = 'bab7b5c48bbb30677f7747fe2445c2673fd663c5',
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $ExpectedDocFlowMain = '7e9dc58947797251ab3be188411f514a6b078ac3'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expectedBranch = $ExpectedTradeOpsBranch
$expectedTradeOpsMain = $ExpectedTradeOpsMain
$expectedDocFlow = $ExpectedDocFlowMain
$expectedSdk = '10.0.401'

function Git-Exact([string] $root, [string[]] $gitArgs) {
    $out = & git -C $root @gitArgs
    if ($LASTEXITCODE -ne 0) { throw "git command failed for $root" }
    return ($out | Out-String).Trim()
}
function Assert-Equal([string] $actual, [string] $expected, [string] $description) {
    if ($actual -cne $expected) { throw "$description expected $expected, received $actual" }
}
function Require-Clean([string] $root) {
    if ((Git-Exact $root @('status','--porcelain','--untracked-files=normal')) -ne '') {
        throw "Dirty tracked/untracked source tree: $root"
    }
}
function Run-Checked([string] $label, [scriptblock] $command) {
    Write-Host "RUN $label"
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$label failed (exit $LASTEXITCODE)" }
}
function Ensure-Net10([string] $path) {
    [xml] $doc = Get-Content -Raw -Encoding UTF8 -LiteralPath $path
    $tfm = @($doc.Project.PropertyGroup | ForEach-Object { $_.TargetFramework } | Where-Object { $_ })
    if ($tfm.Count -ne 1 -or $tfm[0] -ne 'net10.0') {
        throw "Expected net10.0: $path"
    }
}

$tempRoot = $null
$previousDocflowRoot = [Environment]::GetEnvironmentVariable('TO_V2_03_DOCFLOW_ROOT')
$previousDotnetRoot = [Environment]::GetEnvironmentVariable('DOTNET_ROOT')
$previousTestContentRoot = [Environment]::GetEnvironmentVariable('ASPNETCORE_TEST_CONTENTROOT_DOCFLOW_API')
try {
    foreach ($path in @($TradeOpsRoot,$DocFlowRoot)) {
        if (-not (Test-Path -LiteralPath $path -PathType Container)) { throw "Missing checkout: $path" }
    }
    if (-not (Test-Path -LiteralPath $DotNetExe -PathType Leaf)) {
        throw "Missing .NET 10.0.401 executable: $DotNetExe"
    }
    Assert-Equal (Git-Exact $TradeOpsRoot @('branch','--show-current')) $expectedBranch 'TradeOps branch'
    Assert-Equal (Git-Exact $TradeOpsRoot @('rev-parse','HEAD')) $ExpectedTradeOpsHead 'TradeOps HEAD'
    Assert-Equal (Git-Exact $TradeOpsRoot @('rev-parse',"origin/$expectedBranch")) $ExpectedTradeOpsHead 'TradeOps remote HEAD'
    Assert-Equal (Git-Exact $TradeOpsRoot @('rev-parse','origin/main')) $expectedTradeOpsMain 'TradeOps main (reviewed baseline)'
    Assert-Equal (Git-Exact $DocFlowRoot @('branch','--show-current')) 'main' 'DocFlow branch'
    Assert-Equal (Git-Exact $DocFlowRoot @('rev-parse','HEAD')) $expectedDocFlow 'DocFlow main HEAD'
    Assert-Equal (Git-Exact $DocFlowRoot @('rev-parse','origin/main')) $expectedDocFlow 'DocFlow remote main'
    Require-Clean $TradeOpsRoot
    Require-Clean $DocFlowRoot

    $sdk = (& $DotNetExe --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'dotnet --version failed' }
    Assert-Equal $sdk $expectedSdk 'SDK version'
    $globalJson = Get-Content -Raw -Encoding UTF8 (Join-Path $TradeOpsRoot 'global.json') | ConvertFrom-Json
    Assert-Equal $globalJson.sdk.version $expectedSdk 'TradeOps global.json SDK'
    $projects = @(Get-ChildItem (Join-Path $TradeOpsRoot 'src'),(Join-Path $TradeOpsRoot 'tests'),(Join-Path $TradeOpsRoot 'tools') -Recurse -File -Filter '*.csproj')
    if ($projects.Count -ne 15) { throw "TradeOps existing 15-project inventory changed: $($projects.Count)" }
    foreach ($p in $projects) { Ensure-Net10 $p.FullName }
    $docflowApi = Join-Path $DocFlowRoot 'src\DocFlow.Api\DocFlow.Api.csproj'
    $tradeOpsInfra = Join-Path $TradeOpsRoot 'src\TradeOps.Infrastructure\TradeOps.Infrastructure.csproj'
    $docflowFacade = Join-Path $DocFlowRoot 'src\DocFlow.Extraction.Application\DocFlow.Extraction.Application.csproj'
    $docflowNormalize = Join-Path $DocFlowRoot 'src\DocFlow.Normalization\DocFlow.Normalization.csproj'
    $docflowSchema = Join-Path $DocFlowRoot 'src\DocFlow.SchemaExtraction\DocFlow.SchemaExtraction.csproj'
    foreach ($p in @($docflowApi,$tradeOpsInfra,$docflowFacade,$docflowNormalize,$docflowSchema)) {
        if (-not (Test-Path -LiteralPath $p)) { throw "Missing genuine source project: $p" }
        Ensure-Net10 $p
    }
    $source = Join-Path $TradeOpsRoot 'tools\DualHttpContract\Program.cs'
    if (-not (Test-Path -LiteralPath $source)) { throw "Missing contract harness source: $source" }
    if (Test-Path (Join-Path $TradeOpsRoot 'tools\DualHttpContract\DualHttpContract.csproj')) {
        throw 'Harness must not be a checked-in sixteenth .csproj'
    }

    # TestServer must never use a production public address, even if the machine
    # happens to have inherited ASP.NET Core binding environment variables.
    foreach ($key in @('ASPNETCORE_URLS','ASPNETCORE_HTTP_PORTS','HTTP_PORTS','DOTNET_URLS')) {
        if (-not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($key))) {
            throw "Unset inherited $key before isolated offline validation"
        }
    }

    Write-Host "SOURCE TradeOps=$ExpectedTradeOpsHead DocFlow=$expectedDocFlow"
    Write-Host "SDK $sdk | all participating projects net10.0 | TradeOps inventory=15"
    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('to-v2-03-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    $harnessProject = Join-Path $tempRoot 'DualHttpContract.csproj'
    $srcXml = [System.Security.SecurityElement]::Escape($source)
    $toXml = [System.Security.SecurityElement]::Escape($tradeOpsInfra)
    $dfXml = [System.Security.SecurityElement]::Escape($docflowApi)
    # All references go to original projects; nothing is copied or vendored.
    $csproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="$srcXml" Link="Program.cs" />
    <ProjectReference Include="$toXml" />
    <ProjectReference Include="$dfXml" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.12" />
  </ItemGroup>
</Project>
"@
    [IO.File]::WriteAllText($harnessProject, $csproj, [Text.UTF8Encoding]::new($false))
    $sdkJson = '{"sdk":{"version":"10.0.401","rollForward":"disable","allowPrerelease":false}}'
    [IO.File]::WriteAllText((Join-Path $tempRoot 'global.json'), $sdkJson,
        [Text.UTF8Encoding]::new($false))
    $env:DOTNET_ROOT = Split-Path -Parent $DotNetExe
    $env:TO_V2_03_DOCFLOW_ROOT = (Resolve-Path -LiteralPath $DocFlowRoot).Path
    $nativeApiContentRoot = Join-Path $env:TO_V2_03_DOCFLOW_ROOT 'src\DocFlow.Api'
    if (-not (Test-Path -LiteralPath (Join-Path $nativeApiContentRoot 'Program.cs') -PathType Leaf)) {
        throw 'DocFlow.Api content root marker missing; TestServer bootstrap prohibited'
    }
    # This WAF discovery fallback allows a scratch harness outside both .sln
    # trees to find the genuine DocFlow.Api assembly. Each WebApplicationFactory
    # then overrides its *actual* contentRoot with a unique test-only directory
    # containing early-loaded appsettings.json (DF-V2-08 pattern).
    # Restore this setting in finally; no network port is bound.
    $env:ASPNETCORE_TEST_CONTENTROOT_DOCFLOW_API = $nativeApiContentRoot

    # Approved package restore is read-only from public NuGet.org; there is no
    # private feed, package push, provider smoke, database connection or OIDC discovery.
    Run-Checked 'temporary source-linked restore' {
        & $DotNetExe restore $harnessProject --source 'https://api.nuget.org/v3/index.json' -warnaserror
    }
    Run-Checked 'temporary net10 Release build' {
        & $DotNetExe build $harnessProject -c Release --no-restore -warnaserror
    }
    # WebApplicationFactory<DocFlow.Api.Program> requires the genuine web
    # application's dependency manifest alongside the temporary test runner.
    # SDK project references normally copy it; if absent, copy only built
    # metadata from the source-linked project into the disposable sandbox.
    $testBin = Join-Path $tempRoot 'bin\Release\net10.0'
    $depsInSandbox = Join-Path $testBin 'DocFlow.Api.deps.json'
    if (-not (Test-Path -LiteralPath $depsInSandbox -PathType Leaf)) {
        $depsFromBuild = Join-Path $nativeApiContentRoot 'bin\Release\net10.0\DocFlow.Api.deps.json'
        if (-not (Test-Path -LiteralPath $depsFromBuild -PathType Leaf)) {
            throw 'Native DocFlow.Api dependency manifest is unavailable (TestServer cannot start)'
        }
        Copy-Item -LiteralPath $depsFromBuild -Destination $depsInSandbox
        Write-Host 'OFFLINE TESTHOST: copied DocFlow.Api.deps.json to disposable sandbox'
    }
    Write-Host 'OFFLINE TESTHOST: pinned native DocFlow.Api source + isolated per-host JSON content roots; in-memory TestServer only'
    Run-Checked 'in-memory dual-HTTP verification (8 cases)' {
        & $DotNetExe run --project $harnessProject -c Release --no-build --no-restore
    }
    Write-Host "PASS TO-V2-03 exact TradeOps=$ExpectedTradeOpsHead DocFlow=$expectedDocFlow SDK=$sdk"
}
catch {
    Write-Error ("TO-V2-03 NOT PASSED: " + $_.Exception.Message)
    exit 1
}
finally {
    [Environment]::SetEnvironmentVariable('ASPNETCORE_TEST_CONTENTROOT_DOCFLOW_API', $previousTestContentRoot)
    [Environment]::SetEnvironmentVariable('TO_V2_03_DOCFLOW_ROOT', $previousDocflowRoot)
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $previousDotnetRoot)
    if ($null -ne $tempRoot -and (Test-Path -LiteralPath $tempRoot)) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
