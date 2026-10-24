#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string]$ExpectedHead,
    [Parameter(Mandatory=$true)]
    [ValidatePattern('^[A-Z]$')]
    [string]$UsbDrive,
    [string]$Repo = 'D:\Projects\TradeOps-v2'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'WINDOWS_REQUIRED'
}
if (-not (Test-Path -LiteralPath $Repo -PathType Container)) {
    throw 'TRADEOPS_REPOSITORY_MISSING'
}
if ($UsbDrive -eq 'D' -or $UsbDrive -eq 'C' -or $UsbDrive -eq 'E') {
    throw 'TARGET_MUST_BE_DISTINCT_USB_PHYSICAL_DRIVE'
}

$sourceFile = 'D:\TradeOps-Backups\tradeops-pg17-recovery-20261024T102605Z-54389110da99.tar.gpg'
$sourceSidecar = "$sourceFile.sha256"
if (-not (Test-Path -LiteralPath $sourceFile -PathType Leaf) -or
    -not (Test-Path -LiteralPath $sourceSidecar -PathType Leaf)) {
    throw 'QUALIFIED_GPG_SOURCE_AND_SHA256_REQUIRED'
}

# Confirm disk identity before touching USB storage: the destination must
# be a separate USB physical disk, not another partition on the NVMe.
$sourcePartition = Get-Partition -DriveLetter D -ErrorAction Stop
$targetPartition = Get-Partition -DriveLetter $UsbDrive -ErrorAction Stop
$usbDisk = Get-Disk -Number $targetPartition.DiskNumber -ErrorAction Stop
$sourceDisk = Get-Disk -Number $sourcePartition.DiskNumber -ErrorAction Stop
if ($targetPartition.DiskNumber -eq $sourcePartition.DiskNumber) {
    throw 'USB_TARGET_SHARES_ORIGINAL_PHYSICAL_DISK'
}
if ($usbDisk.BusType.ToString() -cne 'USB') {
    throw 'EXTERNAL_USB_BUS_REQUIRED'
}
if ($usbDisk.IsBoot -or $usbDisk.IsSystem -or $usbDisk.IsReadOnly -or $usbDisk.IsOffline) {
    throw 'USB_DISK_NOT_SAFE_FOR_ADD_ONLY_COPY'
}
$usbVolume = Get-Volume -DriveLetter $UsbDrive -ErrorAction Stop
if ($usbVolume.FileSystem -notin @('NTFS','exFAT')) {
    throw 'USB_VOLUME_MUST_BE_NTFS_OR_EXFAT'
}

$basename = [IO.Path]::GetFileName($sourceFile)
$checksumName = "$basename.sha256"
$sidecarLine = [IO.File]::ReadAllText($sourceSidecar).TrimEnd([char]13,[char]10)
$match = [regex]::Match(
    $sidecarLine,
    ('^(?<sha>[a-fA-F0-9]{{64}})  {0}$' -f [regex]::Escape($basename))
)
if (-not $match.Success) {
    throw 'SOURCE_SHA256_SIDECAR_FORMAT_MISMATCH'
}
$expectedSha = $match.Groups['sha'].Value.ToUpperInvariant()
$realSourceSha = (Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256).Hash
if ($realSourceSha -cne $expectedSha) {
    throw 'SOURCE_ENCRYPTED_CHECKSUM_MISMATCH'
}

$required = (Get-Item -LiteralPath $sourceFile).Length + 104857600L
if ($usbVolume.SizeRemaining -le $required) {
    throw 'USB_INSUFFICIENT_FREE_SPACE'
}

$branch = 'TradeOps/to-v2-37-usb-independent-encrypted-recovery'
git.exe -C $Repo fetch origin $branch
if ($LASTEXITCODE -ne 0) { throw 'FETCH_FAILED' }
$actualHead = (git.exe -C $Repo rev-parse FETCH_HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actualHead -cne $ExpectedHead) {
    throw 'EXTERNAL_USB_RECOVERY_SHA_MISMATCH'
}

$staging = Join-Path $env:TEMP ('TradeOps-UsbRecovery-' + $PID + '-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $staging) { throw 'STAGING_COLLISION' }
New-Item -ItemType Directory -Path $staging -ErrorAction Stop | Out-Null
try {
    $gitObject = ('{0}:deploy/deribit-evidence/verify-independent-usb-recovery-pg17.sh' -f $ExpectedHead)
    $sourceCode = @(git.exe -C $Repo show $gitObject)
    if ($LASTEXITCODE -ne 0 -or $sourceCode.Count -eq 0) {
        throw 'PINNED_USB_RECOVERY_SCRIPT_MISSING'
    }
    $scriptPath = Join-Path $staging 'verify-independent-usb-recovery-pg17.sh'
    $utf8 = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText($scriptPath, (($sourceCode -join [char]10) + [char]10), $utf8)
    $portableScript = $scriptPath.Replace([char]92,[char]47)
    $wslScriptLines = @(& wsl.exe -u user -- wslpath -u -a $portableScript)
    if ($LASTEXITCODE -ne 0 -or $wslScriptLines.Count -ne 1) {
        throw 'WSL_SCRIPT_PATH_FAILED'
    }
    $wslScript = [string]$wslScriptLines[0]
    if (-not $wslScript.Trim().StartsWith('/')) { throw 'WSL_SCRIPT_PATH_INVALID' }
    $wslScript = $wslScript.Trim()
    & wsl.exe -u user -- bash -n $wslScript
    if ($LASTEXITCODE -ne 0) { throw 'USB_RECOVERY_SCRIPT_SYNTAX_FAILED' }

    $destRoot = ('{0}:\TradeOps-Backups' -f $UsbDrive)
    if (Test-Path -LiteralPath $destRoot) {
        $attr = (Get-Item -LiteralPath $destRoot -Force).Attributes
        if (($attr -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'USB_BACKUP_DIR_REPARSE_POINT'
        }
    } else {
        New-Item -ItemType Directory -Path $destRoot -ErrorAction Stop | Out-Null
    }
    $destFile = Join-Path $destRoot $basename
    $destSidecar = Join-Path $destRoot $checksumName
    if ((Test-Path -LiteralPath $destFile) -or
        (Test-Path -LiteralPath $destSidecar)) {
        throw 'USB_BACKUP_ALREADY_EXISTS_DO_NOT_OVERWRITE'
    }
    $partialFile = "$destFile.partial"
    $partialSha = "$destSidecar.partial"
    if ((Test-Path -LiteralPath $partialFile) -or
        (Test-Path -LiteralPath $partialSha)) {
        throw 'USB_PARTIAL_ALREADY_EXISTS'
    }

    try {
        Copy-Item -LiteralPath $sourceFile -Destination $partialFile -ErrorAction Stop
        Copy-Item -LiteralPath $sourceSidecar -Destination $partialSha -ErrorAction Stop
        if ((Get-FileHash -LiteralPath $partialFile -Algorithm SHA256).Hash -cne $expectedSha) {
            throw 'USB_CIPHERTEXT_COPY_SHA256_FAILED'
        }
        $copiedSidecar = [IO.File]::ReadAllText($partialSha)
        if ($copiedSidecar -cne [IO.File]::ReadAllText($sourceSidecar)) {
            throw 'USB_SIDECAR_COPY_MISMATCH'
        }
        Move-Item -LiteralPath $partialFile -Destination $destFile -ErrorAction Stop
        Move-Item -LiteralPath $partialSha -Destination $destSidecar -ErrorAction Stop
    } finally {
        # Remove only newly created *partial* copies.
        Remove-Item -LiteralPath $partialFile -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $partialSha -Force -ErrorAction SilentlyContinue
    }

    $portableUsb = $destFile.Replace([char]92,[char]47)
    $wslUsbLines = @(& wsl.exe -u user -- wslpath -u -a $portableUsb)
    if ($LASTEXITCODE -ne 0 -or $wslUsbLines.Count -ne 1) {
        throw 'USB_WSL_PATH_FAILED'
    }
    $wslUsb = [string]$wslUsbLines[0]
    if (-not ($wslUsb.Trim() -match '^/mnt/[a-z]/')) {
        throw 'USB_WSL_MOUNT_INVALID'
    }

    Write-Host ('TRADEOPS_EXTERNAL_USB_RECOVERY_START source_disk=' +
                $sourceDisk.Number + ' usb_disk=' + $usbDisk.Number +
                ' bus=USB pinned_sha=' + $ExpectedHead)
    Write-Host 'Enter your existing GPG recovery passphrase locally when prompted.'
    & wsl.exe -u user -- bash $wslScript $wslUsb.Trim()
    if ($LASTEXITCODE -ne 0) {
        throw ('EXTERNAL_USB_RECOVERY_FAILED exit=' + $LASTEXITCODE)
    }
    Write-Host ('TRADEOPS_EXTERNAL_USB_BACKUP_PATH=' + $destFile)
}
finally {
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
}
