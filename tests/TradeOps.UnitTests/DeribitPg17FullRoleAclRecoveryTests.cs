using System.Diagnostics;
using Xunit;

namespace TradeOps.UnitTests;

/// <summary>
/// Offline test contract for preserving the evidence schema owner and grants
/// when producing a transportable PostgreSQL 17 recovery bundle.
/// </summary>
public sealed class DeribitPg17FullRoleAclRecoveryTests
{
    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory);
             dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TradeOps.sln")))
                return dir.FullName;
        }

        throw new InvalidOperationException("TradeOps repository root not found.");
    }

    [Fact]
    public void WindowsLauncher_PinsExactShaAndChecksWslBashSyntax()
    {
        var ps = File.ReadAllText(Path.Combine(Root(), "scripts",
            "deribit", "Verify-FullEvidencePg17RoleAclRecovery.ps1"));
        Assert.Contains("rev-parse FETCH_HEAD", ps);
        Assert.Contains("$head -cne $ExpectedHead", ps);
        Assert.Contains("git.exe -C $Repo show $object", ps);
        Assert.Contains("Replace([char]92, [char]47)", ps);
        Assert.Contains("bash -n $wslFiles[0]", ps);
        Assert.DoesNotContain("ExecutionPolicy Bypass", ps);
    }

    [Fact]
    public void SqlBootstrap_CreatesOnlyUnusableNoLoginRoles()
    {
        var sql = File.ReadAllText(Path.Combine(Root(), "deploy",
            "deribit-evidence", "restore-known-evidence-roles.pg17.sql"));
        Assert.Contains("NOT_FRESH_PG17_RECOVERY_TARGET", sql);
        Assert.Contains("CREATE ROLE tradeops_evidence_owner", sql);
        Assert.Contains("CREATE ROLE tradeops_evidence_writer", sql);
        Assert.Contains("NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION", sql);
        Assert.Contains("statement_timeout = '15s'", sql);
        Assert.Contains("search_path = pg_catalog", sql);
        Assert.DoesNotContain("PASSWORD", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GRANT ALL", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP ROLE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BackupDoesNotStripOwnerOrGrants_AndTestsTheActualRestore()
    {
        var sh = File.ReadAllText(Path.Combine(Root(), "deploy",
            "deribit-evidence", "backup-restore-full-acl.pg17.sh"));
        Assert.Contains("pg_dump --format=custom", sh);
        Assert.DoesNotContain("pg_dump --format=custom --no-owner", sh);
        Assert.DoesNotContain("pg_dump --format=custom --no-acl", sh);
        Assert.Contains("pg_restore --exit-on-error", sh);
        Assert.Contains("--create --username=postgres --dbname=postgres", sh);
        Assert.Contains("--network none", sh);
        Assert.Contains("--tmpfs /var/lib/postgresql/data", sh);
        Assert.Contains("com.tradeops.acl_recovery_nonce", sh);
        Assert.Contains("OWNER_OR_ACL_RECOVERY_FAILED", sh);
        Assert.Contains("source_writes=0 deribit_calls=0", sh);
        Assert.Contains("sha256sum --check --status", sh);
        Assert.Contains("has_table_privilege", sh);
        Assert.Contains("has_schema_privilege", sh);
        Assert.Contains("has_database_privilege", sh);
        Assert.DoesNotContain("docker compose down", sh);
        Assert.DoesNotContain("docker volume rm", sh);
        Assert.DoesNotContain("docker system prune", sh);
        Assert.DoesNotContain("DROP DATABASE", sh, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER ROLE tradeops_evidence_writer LOGIN", sh);
    }

    [Fact]
    public void BashSyntaxIsValidOnLinux()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Root(), "deploy",
            "deribit-evidence", "backup-restore-full-acl.pg17.sh");
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
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }
}
