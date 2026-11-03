using System.Diagnostics;
using Xunit;

namespace TradeOps.UnitTests;

/// <summary>
/// Offline checks only. The owner must explicitly approve the PostgreSQL
/// role DDL before a separate Windows/WSL command can execute it.
/// </summary>
public sealed class DeribitPersistentEvidenceWriterInstallScriptTests
{
    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory);
             dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TradeOps.sln")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Cannot resolve TradeOps repository root.");
    }

    [Fact]
    public void WindowsEntry_RequiresExactShaAndSeparateOwnerApproval()
    {
        var ps = File.ReadAllText(Path.Combine(Root(), "scripts", "deribit",
            "Install-PersistentEvidenceWriterNoLogin.ps1"));
        Assert.Contains("[ValidateSet('tradeops_evidence')]", ps);
        Assert.Contains("[switch]$ApproveNoLoginWriterInstallation", ps);
        Assert.Contains("EXPLICIT_NOLOGIN_WRITER_OWNER_APPROVAL_REQUIRED", ps);
        Assert.Contains("rev-parse FETCH_HEAD", ps);
        Assert.Contains("$fetched -cne $ExpectedHead", ps);
        Assert.Contains("git.exe -C $Repo show $gitObject", ps);
        Assert.Contains("Replace([char]92,[char]47)", ps);
        Assert.Contains("bash -n $linuxFiles[0]", ps);
        Assert.DoesNotContain("ExecutionPolicy Bypass", ps);
    }

    [Fact]
    public void RoleInstaller_PreflightsOneExactPersistentPg17AndFreshBackup()
    {
        var bash = File.ReadAllText(Path.Combine(Root(), "deploy",
            "deribit-evidence", "install-writer-nologin.owner-gated.pg17.sh"));
        Assert.Contains("I_APPROVE_INSTALL_NOLOGIN_WRITER_TRADEOPS_EVIDENCE", bash);
        Assert.Contains("tradeops-deribit-evidence-pg17-data", bash);
        Assert.Contains("tradeops_evidence|17|5|5|0|0|0|0|0|0", bash);
        Assert.Contains("post-schema-*.dump.sha256", bash);
        Assert.Contains("sha256sum --check --status", bash);
        Assert.Contains("WRITER_ROLE_TRANSACTION_FAILED", bash);
        Assert.Contains("login=DISABLED", bash);
        Assert.Contains("binding_select=YES position_insert=YES", bash);
        Assert.Contains("update_delete=DENIED execution_insert=DENIED", bash);
        Assert.Contains("role_count=1 broker_rows=0 deribit_calls=0", bash);
        Assert.DoesNotContain("docker volume rm", bash);
        Assert.DoesNotContain("docker compose down", bash);
        Assert.DoesNotContain("docker system prune", bash);
        Assert.DoesNotContain("DROP ROLE", bash, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP TABLE", bash, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DERIBIT_LIVE_READONLY_CLIENT_SECRET", bash);
    }

    [Fact]
    public void ProposedRole_IsNoLoginAndSelectPlusPositionInsertOnly()
    {
        var sql = File.ReadAllText(Path.Combine(Root(), "deploy",
            "deribit-evidence", "TO_V2_33_WRITER_ROLE_PROPOSED.sql"));
        Assert.Contains("CREATE ROLE tradeops_evidence_writer", sql);
        Assert.Contains("NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION", sql);
        Assert.Contains("GRANT SELECT ON TABLE broker_evidence.account_bindings", sql);
        Assert.Contains("GRANT INSERT ON TABLE", sql);
        Assert.Contains("WRITER_LEAST_PRIVILEGE_CHECK_FAILED", sql);
        Assert.DoesNotContain("GRANT ALL", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GRANT INSERT ON TABLE broker_evidence.execution_observations",
            sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RoleInstallerBash_ParsesOnLinux()
    {
        if (OperatingSystem.IsWindows()) return;

        var path = Path.Combine(Root(), "deploy",
            "deribit-evidence", "install-writer-nologin.owner-gated.pg17.sh");
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
            throw new InvalidOperationException("Bash syntax check timed out.");
        }
        Assert.True(process.ExitCode == 0,
            "Bash syntax failed: " + process.StandardError.ReadToEnd());
    }
}
