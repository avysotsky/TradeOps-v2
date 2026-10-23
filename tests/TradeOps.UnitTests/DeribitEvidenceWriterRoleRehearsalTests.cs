using System.Diagnostics;
using Xunit;

namespace TradeOps.UnitTests;

/// <summary>
/// Tests scripts and offline constraints only; does not start Docker or access
/// the owner-controlled PostgreSQL/Deribit account during dotnet test.
/// </summary>
public sealed class DeribitEvidenceWriterRoleRehearsalTests
{
    private static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory);
             d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "TradeOps.sln")))
                return d.FullName;
        }

        throw new InvalidOperationException("TradeOps repository root not found.");
    }

    [Fact]
    public void ProposedWriter_HasNoLoginAndOnlyNecessaryTablePrivileges()
    {
        var sql = File.ReadAllText(Path.Combine(Root(), "deploy",
            "deribit-evidence", "TO_V2_33_WRITER_ROLE_PROPOSED.sql"));
        Assert.Contains("NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION", sql);
        Assert.Contains("GRANT CONNECT ON DATABASE tradeops_evidence", sql);
        Assert.Contains("GRANT USAGE ON SCHEMA broker_evidence", sql);
        Assert.Contains("GRANT SELECT ON TABLE broker_evidence.account_bindings", sql);
        Assert.Contains("broker_evidence.position_snapshots,", sql);
        Assert.Contains("broker_evidence.position_observations", sql);
        Assert.Contains("WRITER_LEAST_PRIVILEGE_CHECK_FAILED", sql);
        Assert.Contains("has_table_privilege", sql);
        Assert.Contains("COMMIT;", sql);
        Assert.DoesNotContain("CREATE ROLE tradeops_evidence_writer LOGIN", sql);
        Assert.DoesNotContain("GRANT ALL", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER DEFAULT PRIVILEGES", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Fixture_ActuallyUsesWriterAndVerifiesDeniedOperations()
    {
        var sql = File.ReadAllText(Path.Combine(Root(), "deploy",
            "deribit-evidence", "TO_V2_33_WRITER_PERMISSIONS_SYNTHETIC.sql"));
        Assert.Contains("SET ROLE tradeops_evidence_writer", sql);
        Assert.Contains("INSERT INTO broker_evidence.position_snapshots", sql);
        Assert.Contains("INSERT INTO broker_evidence.position_observations", sql);
        Assert.Contains("WHEN insufficient_privilege", sql);
        Assert.Contains("UNEXPECTED_UPDATE_PERMISSION", sql);
        Assert.Contains("UNEXPECTED_DELETE_PERMISSION", sql);
        Assert.Contains("UNEXPECTED_CREATE_PERMISSION", sql);
        Assert.Contains("UNEXPECTED_BINDING_INSERT_PERMISSION", sql);
        Assert.Contains("UNEXPECTED_EXECUTION_INSERT_PERMISSION", sql);
        Assert.Contains("ROLLBACK;", sql);
    }

    [Fact]
    public void OwnerLauncher_RequiresExactHeadAndWslSyntaxGate()
    {
        var code = File.ReadAllText(Path.Combine(Root(), "scripts",
            "deribit", "Rehearse-EvidenceWriterRolePg17.ps1"));
        Assert.Contains("rev-parse FETCH_HEAD", code);
        Assert.Contains("$fetched -cne $ExpectedHead", code);
        Assert.Contains("git.exe -C $Repo show $sourceRef", code);
        Assert.Contains("Replace([char]92, [char]47)", code);
        Assert.Contains("bash -n $linux[0]", code);
        Assert.DoesNotContain("ExecutionPolicy Bypass", code);
    }

    [Fact]
    public void Rehearsal_IsNetworklessAndDoesNotWritePersistentSource()
    {
        var script = File.ReadAllText(Path.Combine(Root(), "deploy",
            "deribit-evidence", "rehearse-writer-role.pg17.sh"));
        Assert.Contains("SOURCE_ALREADY_HAS_WRITER_OR_DATA", script);
        Assert.Contains("post-schema-*.dump.sha256", script);
        Assert.Contains("sha256sum --check --status", script);
        Assert.Contains("--network none", script);
        Assert.Contains("--tmpfs /var/lib/postgresql/data", script);
        Assert.Contains("com.tradeops.writer_lab_nonce", script);
        Assert.Contains("postgres:17-bookworm", script);
        Assert.Contains("persistent_roles_created=0 persistent_writes=0", script);
        Assert.DoesNotContain("docker compose down", script);
        Assert.DoesNotContain("docker volume rm", script);
        Assert.DoesNotContain("docker system prune", script);
    }

    [Fact]
    public void RehearsalBash_ParsesOnLinux()
    {
        if (OperatingSystem.IsWindows()) return;

        var script = Path.Combine(Root(), "deploy",
            "deribit-evidence", "rehearse-writer-role.pg17.sh");
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
        process.StartInfo.ArgumentList.Add(script);
        Assert.True(process.Start());
        if (!process.WaitForExit(15000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Bash syntax check timed out.");
        }

        Assert.True(process.ExitCode == 0,
            "Bash syntax failure: " + process.StandardError.ReadToEnd());
    }
}
