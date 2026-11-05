#requires -Version 7.0
[CmdletBinding()]
param([ValidateSet('ReadSnapshot','ProvisionReader')][string]$Action='ReadSnapshot')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$mode=if($Action -eq 'ReadSnapshot'){'--read-snapshot'}else{'--provision-reader'}
$payload=$null;$pinSecure=$null;$idSecure=$null
try {
    if($Action -eq 'ReadSnapshot'){
        $pinPath=Join-Path $repo '.tradeops/b3-private-session/qualification-profile.dpapi.json'
        $vault=Join-Path $env:LOCALAPPDATA 'TradeOps/Credentials/Deribit.Mainnet.ReadOnly.dpapi.json'
        foreach($path in @($pinPath,$vault)){
            if((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'REPARSE_POINT_REFUSED'}
        }
        $envelope=Get-Content -LiteralPath $pinPath -Raw|ConvertFrom-Json
        $saved=Get-Content -LiteralPath $vault -Raw|ConvertFrom-Json
        if($envelope.schema -ne 1 -or $envelope.encryption -cne 'WindowsDPAPI-CurrentUser' -or $saved.schema -ne 1 -or $saved.environment -cne 'DeribitMainnetReadOnly'){throw 'PIN_FORMAT_REFUSED'}
        $pinSecure=ConvertTo-SecureString $envelope.payload
        $pin=([Management.Automation.PSCredential]::new('local',$pinSecure)).GetNetworkCredential().Password|ConvertFrom-Json
        $idSecure=ConvertTo-SecureString $saved.client_id_dpapi
        $id=([Management.Automation.PSCredential]::new('local',$idSecure)).GetNetworkCredential().Password
        $fingerprint=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($id))).ToLowerInvariant()
        if($pin.environment -cne 'Mainnet' -or $pin.expectedAccountId -le 0 -or $pin.accountType -cnotin @('main','subaccount') -or $pin.credentialIdSha256 -cne $fingerprint){throw 'ENCRYPTED_PIN_BINDING_REFUSED'}
        $payload=@{ExpectedAccountId=$pin.expectedAccountId;AccountType=$pin.accountType}|ConvertTo-Json -Compress
    }
    $runner='/mnt/d/Projects/TradeOps-v2/scripts/testing/mainnet-recovery-reader.sh'
    $process=[Diagnostics.Process]::new()
    $process.StartInfo=[Diagnostics.ProcessStartInfo]::new('wsl.exe',('-d Ubuntu -u root -- bash '+$runner+' '+$mode))
    $process.StartInfo.UseShellExecute=$false;$process.StartInfo.CreateNoWindow=$true
    $process.StartInfo.RedirectStandardInput=$true;$process.StartInfo.RedirectStandardOutput=$true;$process.StartInfo.RedirectStandardError=$true
    if(!$process.Start()){throw 'READER_START_FAILED'}
    $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
    if($payload){$process.StandardInput.WriteLine($payload)}
    $process.StandardInput.Close()
} finally {
    $payload=$null;$pin=$null;$id=$null;$saved=$null
    if($pinSecure){$pinSecure.Dispose()};if($idSecure){$idSecure.Dispose()}
}
if(!$process.WaitForExit(60000)){throw 'READER_DEADLINE; inspect local evidence'}
$output=$stdout.GetAwaiter().GetResult();$null=$stderr.GetAwaiter().GetResult()
Write-Output $output
if($process.ExitCode -ne 0){throw 'RECOVERY_READER_FAILED; inspect local evidence'}
