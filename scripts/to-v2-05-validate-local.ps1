# TO-V2-05: owner-only, read-only staged NuGet consumer compatibility test.
# 3 = local candidate ABI PASS; publishing and production integration BLOCKED.
# 2 = FAIL or incomplete evidence; never 0. GitHub Actions remain OFF.
[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedHead,
 [Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$StageDir,
 [string]$TradeOpsRoot='D:\Projects\TradeOps-v2',
 [string]$DocFlowRoot='D:\Projects\DocFlow-v2',
 [string]$DotNetExe='D:\DotNet\dotnet.exe'
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$branch='TradeOps/to-v2-05-offline-docflow-package-consumer-gate'
$toMain='0b2a5216c5528cb49d41eb9f5a43870e935da3ef'
$dfMain='62fa3c3c77134cf923233e51fcba7c4f08eb2ef7'
$source='927eddee08d084e7759eef1b0a67404711d2b3b1'
$version='2.0.0-preview.1'
$ids=@('DocFlow.Normalization','DocFlow.SchemaExtraction','DocFlow.Extraction.Application')
$names=@(foreach($id in $ids) { "$id.$version.nupkg"; "$id.$version.snupkg" })
$cases=@('ABI-01-source-assembly-boundary','ABI-02-raw-mapping-and-canonical-artifacts',
 'ABI-03-owned-json-lifetime','ABI-04-pre-cancellation','ABI-05-selector-rejection',
 'ABI-06-malformed-input-rejection','ABI-07-invalid-structured-result')
function Equal([string]$actual,[string]$expected,[string]$label) {
 if($actual -cne $expected) { throw "$label mismatch: expected=$expected actual=$actual" }
}
function Git([string]$root,[string[]]$argsForGit) {
 $result=& git -C $root @argsForGit
 if($LASTEXITCODE -ne 0) { throw "Git preflight failed: $root" }
 return ($result | Out-String).Trim()
}
function Clean([string]$root) {
 Equal (Git $root @('status','--porcelain','--untracked-files=normal')) '' "clean tree $root"
}
function Origin([string]$root,[string]$repo) {
 $url=Git $root @('remote','get-url','origin')
 $p=[regex]::Escape($repo)
 if($url -cnotmatch "^https://github\.com/$p(\.git)?/?$" -and
    $url -cnotmatch "^git@github\.com:$p(\.git)?/?$" -and
    $url -cnotmatch "^ssh://git@github\.com/$p(\.git)?/?$") {
  throw "Unexpected GitHub origin: $root"
 }
}
function NoReparse([string]$where) {
 $path=[IO.Path]::GetFullPath($where)
 while($true) {
  if(Test-Path -LiteralPath $path) {
   $item=Get-Item -LiteralPath $path -Force
   if(($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Symlink or junction forbidden: $path"
   }
  }
  $parent=[IO.Path]::GetDirectoryName($path.TrimEnd([char[]]@('\','/')))
  if([string]::IsNullOrEmpty($parent) -or $parent -eq $path) { break }
  $path=$parent
 }
}
function Within([string]$child,[string]$ancestor) {
 $a=[IO.Path]::GetFullPath($ancestor).TrimEnd([char[]]@('\','/'))
 $c=[IO.Path]::GetFullPath($child).TrimEnd([char[]]@('\','/'))
 return ($c.Equals($a,[StringComparison]::OrdinalIgnoreCase) -or
         $c.StartsWith($a+'\',[StringComparison]::OrdinalIgnoreCase))
}
function SDKPin([string]$root) {
 $json=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'global.json') | ConvertFrom-Json
 Equal ([string]$json.sdk.version) '10.0.401' "global.json at $root"
}
function DotNet([string]$label,[string[]]$arguments) {
 Write-Host "RUN $label"
 & $DotNetExe @arguments
 if($LASTEXITCODE -ne 0) { throw "$label failed ($LASTEXITCODE)" }
}
function XmlChild([System.Xml.XmlNode]$parent,[string]$name) {
 return $parent.SelectSingleNode("./*[local-name()='$name']")
}
function XmlValue([System.Xml.XmlNode]$parent,[string]$name) {
 $node=XmlChild $parent $name
 if($null -eq $node) { return $null }
 return $node.InnerText.Trim()
}
function InspectArchive([string]$path,[string]$id,[string]$kind,[string]$sha) {
 $sourceFile=[IO.File]::OpenRead($path)
 try {
  $zip=[IO.Compression.ZipArchive]::new($sourceFile,[IO.Compression.ZipArchiveMode]::Read,$false)
  try {
   $entries=@($zip.Entries)
   $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
   $total=[long]0
   $payload="lib/net10.0/$id.$(if($kind -eq 'nupkg'){'dll'}else{'pdb'})"
   foreach($entry in $entries) {
    $name=$entry.FullName
    $total+=$entry.Length
    if(-not $seen.Add($name) -or $name -match '(^/|\\|(^|/)\.{1,2}(/|$))' -or
       $entry.Name -ceq '' -or $entry.Length -gt 134217728 -or $total -gt 268435456) {
     throw "Unsafe, duplicate or oversized ZIP entry: $name"
    }
    if((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) {
     throw "ZIP symlink forbidden: $name"
    }
    $allowed=$name -ceq '[Content_Types].xml' -or $name -ceq '_rels/.rels' -or
       $name -ceq 'README.md' -or $name -ceq "$id.nuspec" -or $name -ceq $payload -or
       $name -cmatch '^package/services/metadata/core-properties/[^/]+\.psmdcp$'
    if(-not $allowed) { throw "Forbidden archive entry: $name" }
    # Force decompression to reject truncated/invalid deflate streams.
    $input=$entry.Open()
    try {
     $buffer=[byte[]]::new(65536)
     while($input.Read($buffer,0,$buffer.Length) -gt 0) { }
    } finally { $input.Dispose() }
   }
   $nuspec=$zip.GetEntry("$id.nuspec")
   if($null -eq $nuspec -or $null -eq $zip.GetEntry($payload)) {
    throw "NuSpec/net10.0 binary absent: $id.$kind"
   }
   $reader=[IO.StreamReader]::new($nuspec.Open(),[Text.Encoding]::UTF8)
   try { [xml]$xml=$reader.ReadToEnd() } finally { $reader.Dispose() }
   $metadata=$xml.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
   if($null -eq $metadata) { throw "NuSpec metadata absent: $id" }
   Equal (XmlValue $metadata 'id') $id 'NuSpec id'
   Equal (XmlValue $metadata 'version') $version 'NuSpec version'
   $authors=XmlValue $metadata 'authors'
   $owners=XmlValue $metadata 'owners'
   $readme=XmlValue $metadata 'readme'
   if($kind -eq 'nupkg' -and ($authors -cne 'avysotsky' -or
      $readme -cne 'README.md' -or $null -eq $zip.GetEntry('README.md'))) {
    throw "Missing package authors/readme: $id"
   }
   if($null -ne $authors -and $authors -cne 'avysotsky') { throw "Wrong package authors: $id" }
   if($null -ne $owners -and $owners -cne 'avysotsky') { throw "Wrong package owners: $id" }
   if($null -ne $readme -and $readme -cne 'README.md') { throw "Wrong package readme: $id" }
   if([string]::IsNullOrWhiteSpace((XmlValue $metadata 'description')) -or
      (XmlValue $metadata 'copyright') -notmatch 'All rights reserved') {
    throw "Missing proprietary metadata: $id"
   }
   if($null -ne (XmlChild $metadata 'license') -or
      $null -ne (XmlChild $metadata 'licenseUrl')) { throw "Unapproved license metadata: $id" }
   $repo=XmlChild $metadata 'repository'
   if($null -eq $repo -or
      $repo.GetAttribute('url') -cne 'https://github.com/avysotsky/DocFlow-v2' -or
      $repo.GetAttribute('type') -cne 'git' -or
      $repo.GetAttribute('commit') -cne $source) {
    throw "Wrong/stale package source SHA: $id"
   }
   $expected=switch($id) {
    'DocFlow.Normalization' { @() }
    'DocFlow.SchemaExtraction' { @('DocFlow.Normalization') }
    'DocFlow.Extraction.Application' { @('DocFlow.Normalization','DocFlow.SchemaExtraction') }
   }
   $dependencies=@($metadata.SelectNodes(".//*[local-name()='dependency']"))
   if($dependencies.Count -ne $expected.Count) { throw "Wrong dependency count: $id" }
   $found=@()
   foreach($dep in $dependencies) {
    $depName=$dep.GetAttribute('id')
    if($depName -cnotin $expected -or $dep.GetAttribute('version') -cne "[$version]") {
     throw "Unexpected/floating dependency: $id to $depName"
    }
    $found+=,$depName
   }
   if(@($found | Sort-Object -Unique).Count -ne $found.Count) { throw "Duplicate dependency: $id" }
   foreach($group in @($metadata.SelectNodes(".//*[local-name()='group']"))) {
    if($group.GetAttribute('targetFramework').ToLowerInvariant() -cnotin @('net10.0','.net10.0')) {
     throw "Wrong NuGet dependency TFM: $id"
    }
   }
   $binaryStream=$zip.GetEntry($payload).Open()
   try {
    $bytes=[byte[]]::new(4)
    if($binaryStream.Read($bytes,0,4) -ne 4) { throw "Truncated payload: $id" }
    $magic=[Text.Encoding]::ASCII.GetString($bytes)
    if(($kind -eq 'snupkg' -and $magic -cne 'BSJB') -or
       ($kind -eq 'nupkg' -and -not $magic.StartsWith('MZ',[StringComparison]::Ordinal))) {
     throw "Unexpected assembly/PDB header: $id"
    }
   } finally { $binaryStream.Dispose() }
   Write-Host "ARCHIVE VERIFIED $id.$version.$kind SHA256=$sha"
  } finally { $zip.Dispose() }
 } finally { $sourceFile.Dispose() }
}
$oldCache=$env:NUGET_PACKAGES
$oldRoot=$env:DOTNET_ROOT
$oldNoLogo=$env:DOTNET_NOLOGO
$oldTelemetry=$env:DOTNET_CLI_TELEMETRY_OPTOUT
$scratch=$null
try {
 foreach($root in @($TradeOpsRoot,$DocFlowRoot)) {
  if(-not (Test-Path -LiteralPath $root -PathType Container)) { throw "Missing checkout: $root" }
  NoReparse $root
 }
 if(-not (Test-Path -LiteralPath $DotNetExe -PathType Leaf)) { throw 'Missing .NET SDK' }
 Origin $TradeOpsRoot 'avysotsky/TradeOps-v2'
 Origin $DocFlowRoot 'avysotsky/DocFlow-v2'
 Equal (Git $TradeOpsRoot @('branch','--show-current')) $branch 'TradeOps feature branch'
 Equal (Git $TradeOpsRoot @('rev-parse','--abbrev-ref','--symbolic-full-name','@{upstream}')) "origin/$branch" 'feature upstream'
 Equal (Git $TradeOpsRoot @('rev-parse','HEAD')) $ExpectedHead 'feature HEAD'
 Equal (Git $TradeOpsRoot @('rev-parse',"origin/$branch")) $ExpectedHead 'remote feature HEAD'
 Equal (Git $TradeOpsRoot @('rev-parse','origin/main')) $toMain 'TradeOps main'
 Equal (Git $DocFlowRoot @('branch','--show-current')) 'main' 'read-only DocFlow branch'
 Equal (Git $DocFlowRoot @('rev-parse','--abbrev-ref','--symbolic-full-name','@{upstream}')) 'origin/main' 'DocFlow upstream'
 Equal (Git $DocFlowRoot @('rev-parse','HEAD')) $dfMain 'DocFlow main'
 Equal (Git $DocFlowRoot @('rev-parse','origin/main')) $dfMain 'remote DocFlow main'
 Clean $TradeOpsRoot
 Clean $DocFlowRoot
 SDKPin $TradeOpsRoot
 SDKPin $DocFlowRoot
 $sdk=(& $DotNetExe --version | Out-String).Trim()
 if($LASTEXITCODE -ne 0) { throw 'SDK version command failed' }
 Equal $sdk '10.0.401' 'SDK'
 $projects=@(foreach($folder in @('src','tests','tools')) {
  Get-ChildItem -LiteralPath (Join-Path $TradeOpsRoot $folder) -Recurse -File -Filter '*.csproj'
 })
 if($projects.Count -ne 15) { throw "Changed project count: $($projects.Count), required 15" }
 if(Test-Path -LiteralPath (Join-Path $TradeOpsRoot 'tools\DocFlowCandidateConsumerProbe\Consumer.csproj')) {
  throw 'Sixteenth project forbidden'
 }
 foreach($proj in $projects) {
  [xml]$xml=Get-Content -LiteralPath $proj.FullName -Raw -Encoding UTF8
  $tfm=@($xml.Project.PropertyGroup | ForEach-Object { $_.TargetFramework } | Where-Object { $_ })
  if($tfm.Count -ne 1 -or $tfm[0] -cne 'net10.0') {
   throw "Non-net10.0 project: $($proj.FullName)"
  }
 }
 $stage=[IO.Path]::GetFullPath($StageDir)
 NoReparse $stage
 if(-not (Test-Path -LiteralPath $stage -PathType Container)) { throw "Missing stage: $stage" }
 $globalCache=Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages'
 foreach($protected in @($TradeOpsRoot,$DocFlowRoot,$globalCache)) {
  if((Within $stage $protected) -or (Within $protected $stage)) {
   throw "Stage intersects protected source/cache: $protected"
  }
 }
 if(-not [string]::IsNullOrWhiteSpace($oldCache) -and
    ((Within $stage $oldCache) -or (Within $oldCache $stage))) {
  throw 'Stage intersects inherited NuGet cache'
 }
 $manifestPath=Join-Path $stage 'manifest.json'
 $feed=Join-Path $stage 'artifacts'
 NoReparse $manifestPath
 NoReparse $feed
 if(-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $feed -PathType Container)) {
  throw 'Manifest or artifacts folder missing'
 }
 $manifest=Get-Content -Raw -Encoding UTF8 -LiteralPath $manifestPath | ConvertFrom-Json
 Equal ([string]$manifest.owner) 'avysotsky' 'manifest owner'
 Equal ([string]$manifest.source_sha) $source 'immutable candidate provenance'
 Equal ([string]$manifest.version) $version 'package version'
 if((@($manifest.PSObject.Properties.Name | Sort-Object) -join '|') -cne
    (@(@('owner','sha256','source_sha','version') | Sort-Object) -join '|')) {
  throw 'Missing/extra manifest fields'
 }
 $archives=@(Get-ChildItem -LiteralPath $feed -Force)
 if($archives.Count -ne 6 -or
    (@($archives.Name | Sort-Object) -join '|') -cne (@($names | Sort-Object) -join '|')) {
  throw 'Expected precisely six staged archives'
 }
 $inventory=@($manifest.sha256.PSObject.Properties)
 if($inventory.Count -ne 6 -or
    (@($inventory.Name | Sort-Object) -join '|') -cne (@($names | Sort-Object) -join '|')) {
  throw 'Manifest hash coordinates mismatch'
 }
 Add-Type -AssemblyName System.IO.Compression
 foreach($id in $ids) {
  foreach($kind in @('nupkg','snupkg')) {
   $name="$id.$version.$kind"
   $path=Join-Path $feed $name
   NoReparse $path
   if(-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing $name" }
   $hash=[string]$manifest.sha256.PSObject.Properties[$name].Value
   if($hash -cnotmatch '^[0-9a-f]{64}$') { throw "Invalid SHA-256 inventory for $name" }
   $actual=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
   Equal $actual $hash "actual ZIP bytes $name"
   InspectArchive $path $id $kind $actual
  }
 }
 Write-Host "PIN TradeOps=$ExpectedHead main=$toMain DocFlowMain=$dfMain candidate=$source SDK=$sdk"
 $scratch=Join-Path ([IO.Path]::GetTempPath()) ("to-v2-05-"+[guid]::NewGuid().ToString('N'))
 [void](New-Item -ItemType Directory -Path $scratch -ErrorAction Stop)
 NoReparse $scratch
 foreach($protected in @($TradeOpsRoot,$DocFlowRoot,$stage)) {
  if((Within $scratch $protected) -or (Within $protected $scratch)) {
   throw 'Temporary scratch overlaps protected source or staging'
  }
 }
 $consumer=Join-Path $scratch 'consumer'
 $cache=Join-Path $scratch 'packages'
 [void](New-Item -ItemType Directory -Path $consumer)
 [void](New-Item -ItemType Directory -Path $cache)
 $program=Join-Path $TradeOpsRoot 'tools\DocFlowCandidateConsumerProbe\Program.cs'
 if(-not (Test-Path -LiteralPath $program -PathType Leaf)) { throw 'Missing synthetic Program.cs' }
 Copy-Item -LiteralPath $program -Destination (Join-Path $consumer 'Program.cs')
 $app=Join-Path $TradeOpsRoot 'src\TradeOps.Application\TradeOps.Application.csproj'
 if(-not (Test-Path -LiteralPath $app -PathType Leaf)) { throw 'Missing TradeOps.Application source project' }
 $appXml=[Security.SecurityElement]::Escape($app)
 $feedXml=[Security.SecurityElement]::Escape($feed)
 $projectPath=Join-Path $consumer 'Consumer.csproj'
 $project=@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
  <ItemGroup><ProjectReference Include="$appXml" /></ItemGroup>
  <ItemGroup>
    <PackageReference Include="DocFlow.Normalization" Version="[$version]" />
    <PackageReference Include="DocFlow.SchemaExtraction" Version="[$version]" />
    <PackageReference Include="DocFlow.Extraction.Application" Version="[$version]" />
  </ItemGroup>
</Project>
"@
 [IO.File]::WriteAllText($projectPath,$project,[Text.UTF8Encoding]::new($false))
 $configPath=Join-Path $scratch 'NuGet.Config'
 $config=@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources><clear/><add key="candidate-local" value="$feedXml" /><add key="approved-nugetorg" value="https://api.nuget.org/v3/index.json" /></packageSources>
  <packageSourceMapping>
    <packageSource key="candidate-local"><package pattern="DocFlow.*" /></packageSource>
    <packageSource key="approved-nugetorg"><package pattern="Microsoft.*" /><package pattern="System.*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@
 [IO.File]::WriteAllText($configPath,$config,[Text.UTF8Encoding]::new($false))
 $env:NUGET_PACKAGES=$cache
 $env:DOTNET_ROOT=[IO.Path]::GetDirectoryName($DotNetExe)
 $env:DOTNET_NOLOGO='1'
 $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
 DotNet 'isolated package-source-mapped restore' @('restore',$projectPath,'--configfile',$configPath,
  '-p:RestorePackagesWithLockFile=true','-warnaserror')
 $lockPath=Join-Path $consumer 'packages.lock.json'
 if(-not (Test-Path -LiteralPath $lockPath -PathType Leaf)) { throw 'Missing packages.lock.json' }
 $lock=Get-Content -Raw -Encoding UTF8 -LiteralPath $lockPath | ConvertFrom-Json
 $dependencies=@($lock.dependencies.'net10.0'.PSObject.Properties.Name)
 if(@($dependencies | Where-Object { $_ -like 'DocFlow.*' }).Count -ne 3 -or
    @($dependencies | Where-Object {
     $_ -notlike 'DocFlow.*' -and $_ -notlike 'Microsoft.*' -and $_ -notlike 'System.*'
    }).Count -gt 0) { throw "Unexpected NuGet closure: $($dependencies -join ',')" }
 foreach($id in $ids) {
  $item=$lock.dependencies.'net10.0'.PSObject.Properties[$id].Value
  if($null -eq $item -or [string]$item.resolved -cne $version -or
     [string]::IsNullOrWhiteSpace([string]$item.contentHash)) {
   throw "Missing exact package lock: $id"
  }
 }
 $lockBytes=[IO.File]::ReadAllBytes($lockPath)
 $lock.dependencies.'net10.0'.'DocFlow.Extraction.Application'.resolved='99.0.0'
 [IO.File]::WriteAllText($lockPath,($lock | ConvertTo-Json -Depth 80),[Text.UTF8Encoding]::new($false))
 $negativeLog=Join-Path $scratch 'negative-lock.log'
 $negative=Start-Process -FilePath $DotNetExe -ArgumentList @(
  'restore',('"' + $projectPath + '"'),'--configfile',('"' + $configPath + '"'),
  '--locked-mode','--force','-warnaserror'
 ) -Wait -PassThru -NoNewWindow -RedirectStandardOutput $negativeLog
 if($negative.ExitCode -eq 0) { throw 'Tampered lock restored successfully (fail closed)' }
 Write-Host 'PASS ABI-LOCK-TAMPER rejected'
 [IO.File]::WriteAllBytes($lockPath,$lockBytes)
 Remove-Item -LiteralPath (Join-Path $consumer 'obj') -Recurse -Force -ErrorAction SilentlyContinue
 DotNet 'exact locked restore' @('restore',$projectPath,'--configfile',$configPath,'--locked-mode','-warnaserror')
 foreach($id in $ids) {
  $file=[IO.File]::OpenRead((Join-Path $feed "$id.$version.nupkg"))
  try {
   $zip=[IO.Compression.ZipArchive]::new($file,[IO.Compression.ZipArchiveMode]::Read,$false)
   try {
    $entry=$zip.GetEntry("lib/net10.0/$id.dll")
    if($null -eq $entry) { throw "Missing candidate DLL: $id" }
    $entryStream=$entry.Open()
    $hasher=[Security.Cryptography.SHA256]::Create()
    try {
     $expectedBinary=([BitConverter]::ToString($hasher.ComputeHash($entryStream))).Replace('-','').ToLowerInvariant()
    } finally { $hasher.Dispose(); $entryStream.Dispose() }
   } finally { $zip.Dispose() }
  } finally { $file.Dispose() }
  $installed=Join-Path $cache "$($id.ToLowerInvariant())\$version\lib\net10.0\$id.dll"
  if(-not (Test-Path -LiteralPath $installed -PathType Leaf)) {
   throw "NuGet extracted binary missing: $id"
  }
  Equal ((Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash.ToLowerInvariant()) $expectedBinary "candidate DLL $id"
  Write-Host "PASS PACKAGE DLL $id SHA256=$expectedBinary"
 }
 DotNet 'warnings-as-errors Release build' @('build',$projectPath,'-c','Release',
  '--no-restore','--no-incremental','-warnaserror')
 $lines=@(& $DotNetExe run --project $projectPath -c Release --no-build --no-restore 2>&1 |
  ForEach-Object { [string]$_ })
 $runCode=$LASTEXITCODE
 $lines | ForEach-Object { Write-Host $_ }
 if($runCode -ne 0) { throw "Synthetic consumer failed ($runCode)" }
 $passes=@($lines | Where-Object { $_ -cmatch '^PASS ABI-\d\d-[a-z-]+$' })
 if($passes.Count -ne 7 -or @($passes | Sort-Object -Unique).Count -ne 7) {
  throw "Expected 7 independent cases, got $($passes.Count)"
 }
 foreach($case in $cases) {
  if("PASS $case" -cnotin $passes) { throw "Missing ABI case: $case" }
 }
 $summary=@($lines | Where-Object {
  $_ -cmatch '^TO-V2-05 TOTAL cases=7 passed=7 failed=0 assertions=\d+$'
 })
 if($summary.Count -ne 1) { throw 'Synthetic summary missing or duplicated' }
 if($summary[0] -cnotmatch 'assertions=(\d+)$') { throw 'Missing assertion count' }
 $assertions=[int]$Matches[1]
 if($assertions -lt 40) { throw "Assertion threshold not met: $assertions" }
 $projectText=[IO.File]::ReadAllText($projectPath)
 if($projectText -match 'DocFlow-v2.*csproj' -or
    [regex]::Matches($projectText,'<ProjectReference\s+Include=').Count -ne 1) {
  throw 'Consumer has extra or wrong project references'
 }
 foreach($id in $ids) {
  foreach($ext in @('nupkg','snupkg')) {
   $name="$id.$version.$ext"
   $actual=(Get-FileHash -LiteralPath (Join-Path $feed $name) -Algorithm SHA256).Hash.ToLowerInvariant()
   Equal $actual ([string]$manifest.sha256.PSObject.Properties[$name].Value) "final candidate archive $name"
  }
 }
 Clean $TradeOpsRoot
 Clean $DocFlowRoot
 Equal (Git $TradeOpsRoot @('rev-parse','HEAD')) $ExpectedHead 'final feature HEAD'
 Equal (Git $DocFlowRoot @('rev-parse','HEAD')) $dfMain 'final DocFlow main'
 Write-Host "TO-V2-05 TOTAL cases=7 passed=7 failed=0 assertions=$assertions"
 Write-Host 'LOCAL CANDIDATE ABI/CONTRACT PASS / PUBLISH BLOCKED / IN-PROCESS PRODUCTION INTEGRATION BLOCKED'
 exit 3
}
catch {
 Write-Host ('TO-V2-05 NOT PASSED: ' + $_.Exception.Message) -ForegroundColor Red
 exit 2
}
finally {
 $env:NUGET_PACKAGES=$oldCache
 $env:DOTNET_ROOT=$oldRoot
 $env:DOTNET_NOLOGO=$oldNoLogo
 $env:DOTNET_CLI_TELEMETRY_OPTOUT=$oldTelemetry
 if($null -ne $scratch -and (Test-Path -LiteralPath $scratch)) {
  Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
 }
}
