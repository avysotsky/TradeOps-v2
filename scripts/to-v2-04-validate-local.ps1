# TO-V2-04: owner-operated CURRENT-MAIN exact-source qualification.
# Requires clean, fetched private checkouts. Only NuGet.org package restore may
# access the network; tests use real source-linked in-memory TestServer, no sockets.
# Historical TO-V2-03 defaults and behavior remain unchanged.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $ExpectedTradeOpsHead,
    [string] $TradeOpsRoot = 'D:\Projects\TradeOps-v2',
    [string] $DocFlowRoot = 'D:\Projects\DocFlow-v2',
    [string] $DotNetExe = 'D:\DotNet\dotnet.exe'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expectedBranch = 'TradeOps/to-v2-04-current-main-http-qualification'
$expectedTradeOpsMain = '7fab8a4e862c7fbdc2d717941a189308cd69fc18'
$expectedDocFlowMain = '22741d73a67112eae929d65337818b9b53629c8e'
$expectedSdk = '10.0.401'
$expectedCases = @(
    'canonical-native-parity-utf8',
    'invalid-provider-data',
    'jwt-security-https',
    'request-contract-and-bounds',
    'http-response-and-bounds',
    'backend-errors-and-routing',
    'deadline-and-cancellation',
    'bounded-concurrency'
)

function Git-Exact([string] $root, [string[]] $argsForGit) {
    $output = & git -C $root @argsForGit
    if ($LASTEXITCODE -ne 0) { throw "Git preflight failed for $root" }
    return ($output | Out-String).Trim()
}
function Assert-Equal([string] $actual, [string] $expected, [string] $what) {
    if ($actual -cne $expected) { throw "$what mismatch: expected=$expected actual=$actual" }
}
function Require-Clean([string] $root) {
    if ((Git-Exact $root @('status','--porcelain','--untracked-files=normal')) -ne '') {
        throw "Dirty tracked/untracked source tree: $root"
    }
}
function Require-Origin([string] $root, [string] $repo) {
    $url = Git-Exact $root @('remote','get-url','origin')
    $escaped = [regex]::Escape($repo)
    $https = '^https://github\.com/' + $escaped + '(\.git)?/?$'
    $ssh = '^git@github\.com:' + $escaped + '(\.git)?/?$'
    $sshUrl = '^ssh://git@github\.com/' + $escaped + '(\.git)?/?$'
    if ($url -cnotmatch $https -and $url -cnotmatch $ssh -and $url -cnotmatch $sshUrl) {
        throw "Origin repository mismatch for $root (URL omitted)"
    }
}
function Ensure-Net10([string] $path) {
    [xml] $xml = Get-Content -Raw -Encoding UTF8 -LiteralPath $path
    $frameworks = @($xml.Project.PropertyGroup | ForEach-Object { $_.TargetFramework } |
        Where-Object { $_ })
    if ($frameworks.Count -ne 1 -or $frameworks[0] -ne 'net10.0') {
        throw "Expected exactly net10.0 target framework: $path"
    }
}
function Require-SdkJson([string] $root) {
    $jsonPath = Join-Path $root 'global.json'
    $json = Get-Content -Raw -Encoding UTF8 -LiteralPath $jsonPath | ConvertFrom-Json
    Assert-Equal ([string]$json.sdk.version) $expectedSdk "global.json ($root)"
}
function Run-DotNet([string] $label, [string[]] $dotnetArgs) {
    Write-Host "RUN $label"
    & $DotNetExe @dotnetArgs
    if ($LASTEXITCODE -ne 0) { throw "$label failed (exit $LASTEXITCODE)" }
}

try {
    foreach ($root in @($TradeOpsRoot,$DocFlowRoot)) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) {
            throw "Checkout is missing: $root"
        }
    }
    if (-not (Test-Path -LiteralPath $DotNetExe -PathType Leaf)) {
        throw "dotnet executable missing: $DotNetExe"
    }
    Require-Origin $TradeOpsRoot 'avysotsky/TradeOps-v2'
    Require-Origin $DocFlowRoot 'avysotsky/DocFlow-v2'

    # Exact local branch + upstream, both origin refs, HEADs and clean trees
    # BEFORE any restore or build. Fetch refs beforehand, outside this script.
    Assert-Equal (Git-Exact $TradeOpsRoot @('branch','--show-current')) $expectedBranch 'TradeOps branch'
    Assert-Equal (Git-Exact $TradeOpsRoot @('rev-parse','--abbrev-ref','--symbolic-full-name','@{upstream}')) "origin/$expectedBranch" 'TradeOps upstream'
    Assert-Equal (Git-Exact $TradeOpsRoot @('rev-parse','HEAD')) $ExpectedTradeOpsHead 'TradeOps HEAD'
    Assert-Equal (Git-Exact $TradeOpsRoot @('rev-parse',"origin/$expectedBranch")) $ExpectedTradeOpsHead 'TradeOps origin branch'
    Assert-Equal (Git-Exact $TradeOpsRoot @('rev-parse','origin/main')) $expectedTradeOpsMain 'TradeOps origin/main'
    Assert-Equal (Git-Exact $DocFlowRoot @('branch','--show-current')) 'main' 'DocFlow branch'
    Assert-Equal (Git-Exact $DocFlowRoot @('rev-parse','--abbrev-ref','--symbolic-full-name','@{upstream}')) 'origin/main' 'DocFlow upstream'
    Assert-Equal (Git-Exact $DocFlowRoot @('rev-parse','HEAD')) $expectedDocFlowMain 'DocFlow HEAD'
    Assert-Equal (Git-Exact $DocFlowRoot @('rev-parse','origin/main')) $expectedDocFlowMain 'DocFlow origin/main'
    Require-Clean $TradeOpsRoot
    Require-Clean $DocFlowRoot

    foreach ($key in @('ASPNETCORE_URLS','ASPNETCORE_HTTP_PORTS','HTTP_PORTS','DOTNET_URLS')) {
        if (-not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($key))) {
            throw "Unset inherited $key before offline testing"
        }
    }
    $sdk = (& $DotNetExe --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'dotnet --version failed' }
    Assert-Equal $sdk $expectedSdk 'dotnet SDK'
    Require-SdkJson $TradeOpsRoot
    Require-SdkJson $DocFlowRoot

    $projects = @(
        foreach ($folder in @('src','tests','tools')) {
            $root = Join-Path $TradeOpsRoot $folder
            if (-not (Test-Path -LiteralPath $root -PathType Container)) {
                throw "Missing TradeOps project directory: $root"
            }
            Get-ChildItem -LiteralPath $root -File -Recurse -Filter '*.csproj'
        }
    ) | Sort-Object FullName
    if (@($projects).Count -ne 15) {
        throw "TradeOps inventory changed; required 15 .csproj, found $(@($projects).Count)"
    }
    foreach ($project in $projects) { Ensure-Net10 $project.FullName }

    # TradeOps.sln has 14 projects; MigrationSmoke is excluded. Build all 15
    # explicitly, rather than assuming that a solution build covers them.
    $migration = Join-Path $TradeOpsRoot 'tools\MigrationSmoke\TradeOps.MigrationSmoke.csproj'
    if (-not (Test-Path -LiteralPath $migration -PathType Leaf)) {
        throw 'The fifteenth TradeOps project (MigrationSmoke) is missing'
    }
    $harness = Join-Path $TradeOpsRoot 'tools\DualHttpContract\Program.cs'
    if (-not (Test-Path -LiteralPath $harness -PathType Leaf)) {
        throw 'Native dual-HTTP harness source missing'
    }
    if (Test-Path -LiteralPath (Join-Path $TradeOpsRoot 'tools\DualHttpContract\DualHttpContract.csproj')) {
        throw 'Checked-in sixteenth harness project is forbidden'
    }

    Write-Host "TO-V2-04 PIN TradeOps=$ExpectedTradeOpsHead main=$expectedTradeOpsMain"
    Write-Host "TO-V2-04 PIN DocFlow=$expectedDocFlowMain SDK=$sdk projects=15"
    foreach ($project in $projects) {
        $relativeName = $project.FullName.Substring($TradeOpsRoot.TrimEnd([char[]]@('\','/')).Length).TrimStart([char[]]@('\','/'))
        Run-DotNet "restore $relativeName" @('restore',$project.FullName,
            '--source','https://api.nuget.org/v3/index.json','-warnaserror')
    }
    foreach ($project in $projects) {
        $relativeName = $project.FullName.Substring($TradeOpsRoot.TrimEnd([char[]]@('\','/')).Length).TrimStart([char[]]@('\','/'))
        Run-DotNet "Release build $relativeName" @('build',$project.FullName,
            '-c','Release','--no-restore','--no-incremental','-warnaserror')
    }

    # Preserve the TO-V2-03 engine and its original defaults. Run in a separate
    # PowerShell process so the historical exit(1) is always propagated.
    $legacy = Join-Path $TradeOpsRoot 'scripts\to-v2-03-validate-local.ps1'
    if (-not (Test-Path -LiteralPath $legacy -PathType Leaf)) {
        throw 'Historical source-linked HTTP validator missing'
    }
    $shellName = if ($PSVersionTable.PSEdition -eq 'Core') { 'pwsh.exe' } else { 'powershell.exe' }
    $shellExe = Join-Path $PSHOME $shellName
    if (-not (Test-Path -LiteralPath $shellExe -PathType Leaf)) {
        throw "PowerShell child host unavailable: $shellExe"
    }
    $childArgs = @(
        '-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$legacy,
        '-ExpectedTradeOpsHead',$ExpectedTradeOpsHead,
        '-ExpectedTradeOpsBranch',$expectedBranch,
        '-ExpectedTradeOpsMain',$expectedTradeOpsMain,
        '-ExpectedDocFlowMain',$expectedDocFlowMain,
        '-TradeOpsRoot',$TradeOpsRoot,
        '-DocFlowRoot',$DocFlowRoot,
        '-DotNetExe',$DotNetExe
    )
    $reportLines = New-Object 'System.Collections.Generic.List[string]'
    Write-Host 'RUN eight genuine DocFlow.Api/TestServer + TradeOps HTTP adapter cases'
    & $shellExe @childArgs | ForEach-Object {
        $line = [string]$_
        [void]$reportLines.Add($line)
        Write-Host $line
    }
    $testExit = $LASTEXITCODE
    if ($testExit -ne 0) { throw "Source-linked HTTP validator failed (exit $testExit)" }

    $successNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $summaries = [System.Collections.Generic.List[string]]::new()
    foreach ($line in $reportLines) {
        if ($line -cmatch '^PASS ([a-z0-9-]+)$') {
            if (-not $successNames.Add($Matches[1])) {
                throw "Duplicate HTTP scenario: $($Matches[1])"
            }
        }
        if ($line -cmatch '^TO-V2-03 TOTAL cases=\d+ passed=\d+ failed=\d+ assertions=\d+$') {
            [void]$summaries.Add($line)
        }
    }
    if ($summaries.Count -ne 1) {
        throw "Expected one genuine HTTP aggregate, received $($summaries.Count)"
    }
    if ($summaries[0] -cnotmatch '^TO-V2-03 TOTAL cases=(\d+) passed=(\d+) failed=(\d+) assertions=(\d+)$') {
        throw 'Invalid TestServer summary'
    }
    $caseCount = [int]$Matches[1]
    $passed = [int]$Matches[2]
    $failed = [int]$Matches[3]
    $assertions = [int]$Matches[4]
    if ($caseCount -ne 8 -or $passed -ne 8 -or $failed -ne 0 -or $assertions -lt 171) {
        throw "HTTP qualification failed: cases=$caseCount passed=$passed failed=$failed assertions=$assertions"
    }
    if ($successNames.Count -ne 8) { throw "Expected exactly eight per-case PASS lines, got $($successNames.Count)" }
    foreach ($name in $expectedCases) {
        if (-not $successNames.Contains($name)) { throw "Missing HTTP PASS scenario: $name" }
    }

    Require-Clean $TradeOpsRoot
    Require-Clean $DocFlowRoot
    Assert-Equal (Git-Exact $TradeOpsRoot @('rev-parse','HEAD')) $ExpectedTradeOpsHead 'final TradeOps HEAD'
    Assert-Equal (Git-Exact $DocFlowRoot @('rev-parse','HEAD')) $expectedDocFlowMain 'final DocFlow HEAD'
    Write-Host "TO-V2-04 TOTAL cases=8 passed=8 failed=0 assertions=$assertions"
    Write-Host "PASS TO-V2-04 exact TradeOps=$ExpectedTradeOpsHead DocFlow=$expectedDocFlowMain SDK=$sdk 15-project Release build"
}
catch {
    Write-Error ('TO-V2-04 NOT PASSED: ' + $_.Exception.Message)
    exit 1
}
