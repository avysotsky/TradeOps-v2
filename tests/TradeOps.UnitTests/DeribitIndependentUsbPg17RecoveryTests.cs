using System.Diagnostics;
using Xunit;

namespace TradeOps.UnitTests;

/// <summary>
/// Offline safety assertions only. They neither require a USB drive nor
/// decrypt files or touch PostgreSQL during dotnet test.
/// </summary>
public sealed class DeribitIndependentUsbPg17RecoveryTests
{
    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory);
             dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TradeOps.sln")))
                return dir.FullName;
        }
        throw new InvalidOperationException("TradeOps root could not be located.");
    }

    [Fact]
    public void UsbEntry_RequiresDifferentPhysicalUsbDiskAndPinnedSource()
    {
        var code = File.ReadAllText(Path.Combine(Root(), "scripts",
            "deribit", "Copy-AndVerifyEvidenceRecoveryUsb.ps1"));
        Assert.Contains("Get-Partition -DriveLetter D", code);
        Assert.Contains("Get-Partition -DriveLetter $UsbDrive", code);
        Assert.Contains("USB_TARGET_SHARES_ORIGINAL_PHYSICAL_DISK", code);
        Assert.Contains("EXTERNAL_USB_BUS_REQUIRED", code);
        Assert.Contains("USB_BACKUP_ALREADY_EXISTS_DO_NOT_OVERWRITE", code);
        Assert.Contains("USB_INSUFFICIENT_FREE_SPACE", code);
        Assert.Contains("Get-FileHash -LiteralPath $partialFile -Algorithm SHA256", code);
        Assert.Contains("USB_CIPHERTEXT_COPY_SHA256_FAILED", code);
        Assert.Contains("rev-parse FETCH_HEAD", code);
        Assert.Contains("$actualHead -cne $ExpectedHead", code);
        Assert.Contains("git.exe -C $Repo show $gitObject", code);
        Assert.Contains("bash -n $wslScript", code);
        Assert.DoesNotContain("ExecutionPolicy Bypass", code);
        Assert.DoesNotContain("Remove-Item -LiteralPath $sourceFile", code);
        Assert.DoesNotContain("Format-Volume", code);
        Assert.DoesNotContain("Clear-Disk", code);
    }

    [Fact]
    public void UsbRecovery_DecryptsDirectlyFromUsbAndRestoresInIsolatedPg17()
    {
        var script = File.ReadAllText(Path.Combine(Root(), "deploy",
            "deribit-evidence", "verify-independent-usb-recovery-pg17.sh"));
        Assert.Contains("sha256sum --check --status -- \"$(basename -- \"$sidecar\")\"", script);
        Assert.Contains("read -r -s pass </dev/tty", script);
        Assert.Contains("--passphrase-fd 3", script);
        Assert.Contains("--decrypt --output - \"$cipher\"", script);
        Assert.Contains("sha256sum --check --status -- SHA256SUMS", script);
        Assert.Contains("--network none", script);
        Assert.Contains("--tmpfs /var/lib/postgresql/data", script);
        Assert.Contains("postgres:17-bookworm", script);
        Assert.Contains("com.tradeops.usb_recovery_nonce", script);
        Assert.Contains("pg_restore --exit-on-error", script);
        Assert.Contains("--create --username=postgres --dbname=postgres", script);
        Assert.Contains("has_database_privilege", script);
        Assert.Contains("has_schema_privilege", script);
        Assert.Contains("has_table_privilege", script);
        Assert.Contains("source_pg_writes=0 deribit_calls=0", script);
        Assert.DoesNotContain("docker volume rm", script);
        Assert.DoesNotContain("docker compose down", script);
        Assert.DoesNotContain("docker system prune", script);
        Assert.DoesNotContain("DROP TABLE", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--no-acl", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--no-owner", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UsbRecoveryBashSyntax_IsValidOnLinux()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Root(), "deploy",
            "deribit-evidence", "verify-independent-usb-recovery-pg17.sh");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "bash",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }
        };
        process.StartInfo.ArgumentList.Add("-n");
        process.StartInfo.ArgumentList.Add(path);
        Assert.True(process.Start());
        if (!process.WaitForExit(15000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("USB verifier Bash check timed out.");
        }
        Assert.True(process.ExitCode == 0,
            "USB verifier Bash syntax: " + process.StandardError.ReadToEnd());
    }
}
