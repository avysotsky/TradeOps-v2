using System.Diagnostics;
using Xunit;

namespace TradeOps.UnitTests;

/// <summary>
/// Never invokes a database or broker; asserts safety of the distinct
/// owner-authorized persistent schema migration entry points.
/// </summary>
public sealed class DeribitPersistentEvidenceMigrationTests
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
    public void WindowsEntry_RequiresOwnerApprovalAndPinnedRemoteHead()
    {
        var script = File.ReadAllText(Path.Combine(Root(), "scripts", "deribit",
            "Apply-PersistentBrokerEvidenceSchemaPg17.ps1"));
        Assert.Contains("EXPLICIT_OWNER_MIGRATION_APPROVAL_REQUIRED", script);
        Assert.Contains("[switch]$ApproveMigration", script);
        Assert.Contains("[ValidateSet('tradeops_evidence')]", script);
        Assert.Contains("rev-parse FETCH_HEAD", script);
        Assert.Contains("$fetched -cne $ExpectedHead", script);
        Assert.Contains("git.exe -C $Repo show $gitObject", script);
        Assert.Contains("Replace([char]92, [char]47)", script);
        Assert.Contains("bash -n $linuxPaths[0]", script);
        Assert.DoesNotContain("ExecutionPolicy Bypass", script);
    }

    [Fact]
    public void PersistentMigration_IsSchemaOnlyTransactionalAndGuarded()
    {
        var script = File.ReadAllText(Path.Combine(Root(), "deploy",
            "deribit-evidence", "apply-evidence-schema.owner-gated.pg17.sh"));
        Assert.Contains("I_APPROVE_SCHEMA_ONLY_TRADEOPS_EVIDENCE_PG17", script);
        Assert.Contains("tradeops-deribit-evidence-pg17-data", script);
        Assert.Contains("SOURCE_ALREADY_MIGRATED_OR_ROLE_COLLISION", script);
        Assert.Contains("sha256sum --check --status", script);
        Assert.Contains("NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE", script);
        Assert.Contains("SET ROLE tradeops_evidence_owner", script);
        Assert.Contains("RESET ROLE", script);
        Assert.Contains("REVOKE CREATE ON DATABASE tradeops_evidence", script);
        Assert.Contains("awk '$0 != \"BEGIN;\" && $0 != \"COMMIT;\" { print }'", script);
        Assert.Contains("PERSISTENT_MIGRATION_TRANSACTION_FAILED", script);
        Assert.Contains("account_bindings=0 positions=0 executions=0", script);
        Assert.DoesNotContain("docker volume rm", script);
        Assert.DoesNotContain("docker compose down", script);
        Assert.DoesNotContain("docker system prune", script);
        Assert.DoesNotContain("deribit.com/api", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP TABLE", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MigrationBash_ParsesOnLinux()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Root(), "deploy", "deribit-evidence",
            "apply-evidence-schema.owner-gated.pg17.sh");
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
            throw new InvalidOperationException("Migration Bash syntax check timeout.");
        }
        Assert.True(p.ExitCode == 0,
            "Migration Bash syntax error: " + p.StandardError.ReadToEnd());
    }
}
