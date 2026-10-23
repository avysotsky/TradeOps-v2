using System.Diagnostics;
using Xunit;

namespace TradeOps.UnitTests;

/// <summary>
/// Offline safeguards only. Does not operate Docker or inspect a real DB.
/// </summary>
public sealed class DeribitPersistentSchemaBackupScriptTests
{
    private static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "TradeOps.sln")))
                return d.FullName;
        }
        throw new InvalidOperationException("TradeOps repository root not found.");
    }

    [Fact]
    public void WindowsLauncher_RequiresExactRemoteShaAndSafeWslPath()
    {
        var code = File.ReadAllText(Path.Combine(Root(), "scripts", "deribit",
            "Verify-PersistentEvidenceSchemaBackup.ps1"));
        Assert.Contains("rev-parse FETCH_HEAD", code);
        Assert.Contains("$fetched -cne $ExpectedHead", code);
        Assert.Contains("git.exe -C $Repo show $obj", code);
        Assert.Contains("Replace([char]92, [char]47)", code);
        Assert.Contains("$paths.Count -ne 1", code);
        Assert.Contains("bash -n $wslPath", code);
        Assert.DoesNotContain("ExecutionPolicy Bypass", code);
    }

    [Fact]
    public void SchemaBackup_IsReadOnlyAndNetworklessRestoreIsNonceScoped()
    {
        var code = File.ReadAllText(Path.Combine(Root(), "deploy",
            "deribit-evidence", "backup-verify-schema-pg17.sh"));
        Assert.Contains("tradeops-deribit-evidence-pg17-data", code);
        Assert.Contains("SOURCE_SCHEMA_OR_DATA_UNEXPECTED", code);
        Assert.Contains("pg_dump --format=custom", code);
        Assert.Contains("pg_restore --exit-on-error", code);
        Assert.Contains("--network none", code);
        Assert.Contains("--tmpfs /var/lib/postgresql/data", code);
        Assert.Contains("com.tradeops.schema_backup_nonce", code);
        Assert.Contains("RESTORED_SCHEMA_INCORRECT", code);
        Assert.Contains("sha256sum --check --status", code);
        Assert.Contains("source_writes=0 deribit_calls=0", code);
        Assert.DoesNotContain("docker volume rm", code);
        Assert.DoesNotContain("docker compose down", code);
        Assert.DoesNotContain("docker system prune", code);
        Assert.DoesNotContain("DROP DATABASE", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE ROLE", code, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SchemaBackupBash_ParsesOnLinux()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Root(), "deploy", "deribit-evidence",
            "backup-verify-schema-pg17.sh");
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "bash",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }
        };
        p.StartInfo.ArgumentList.Add("-n");
        p.StartInfo.ArgumentList.Add(path);
        Assert.True(p.Start());
        if (!p.WaitForExit(15000))
        {
            p.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Bash syntax gate timeout.");
        }
        Assert.True(p.ExitCode == 0, p.StandardError.ReadToEnd());
    }
}
