using System.Diagnostics;
using Xunit;

namespace TradeOps.UnitTests;

/// <summary>
/// Offline constraints for SQL rehearsal of persistent broker evidence.
/// These tests never connect to real PostgreSQL or call Deribit.
/// </summary>
public sealed class DeribitRealEvidenceSchemaRehearsalTests
{
    private static string Root()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "TradeOps.sln")))
                return current.FullName;
        }
        throw new InvalidOperationException("Repository not found.");
    }

    [Fact]
    public void WindowsLauncher_PinsCommitAndValidatesWslPathAndBash()
    {
        var script = File.ReadAllText(Path.Combine(Root(), "scripts",
            "deribit", "Rehearse-BrokerEvidenceSchemaPg17.ps1"));
        Assert.Contains("rev-parse FETCH_HEAD", script);
        Assert.Contains("$actual -cne $ExpectedHead", script);
        Assert.Contains("git.exe -C $Repo show $sourceRef", script);
        Assert.Contains("Replace([char]92, [char]47)", script);
        Assert.Contains("wslpath -u -a", script);
        Assert.Contains("bash -n $wslPaths[0]", script);
        Assert.Contains("TRADEOPS_PG17_SCHEMA_REHEARSAL_START", script);
        Assert.DoesNotContain("ExecutionPolicy Bypass", script);
    }

    [Fact]
    public void Rehearsal_UsesDisposableNoNetworkPg17Only()
    {
        var script = File.ReadAllText(Path.Combine(Root(), "deploy",
            "deribit-evidence", "rehearse-evidence-schema.pg17.sh"));
        Assert.Contains("tradeops-deribit-evidence-pg17-data", script);
        Assert.Contains("[[ \"$identity\" == 'tradeops_evidence|17|0' ]] || fail SOURCE_HAS_SCHEMA", script);
        Assert.Contains("sha256sum --check --status", script);
        Assert.Contains("--network none", script);
        Assert.Contains("--tmpfs /var/lib/postgresql/data", script);
        Assert.Contains("postgres:17-bookworm", script);
        Assert.Contains("tradeops_schema_rehearsal", script);
        Assert.Contains("persistent_writes=0 broker_calls=0", script);
        Assert.Contains("com.tradeops.rehearsal_nonce", script);
        Assert.DoesNotContain("docker volume rm", script);
        Assert.DoesNotContain("docker compose down", script);
        Assert.DoesNotContain("docker system prune", script);
    }

    [Fact]
    public void SqlFixture_DoesNotContainAccountDataOrDestructiveMigration()
    {
        var path = Path.Combine(Root(), "deploy", "deribit-evidence",
            "verify-schema-invariants.synthetic.sql");
        var fixture = File.ReadAllText(path);
        Assert.Contains("BEGIN;", fixture);
        Assert.Contains("ROLLBACK;", fixture);
        Assert.Contains("unique_violation", fixture);
        Assert.Contains("native_size=-100", fixture);
        Assert.Contains("signed_fee=-0.00001", fixture);
        Assert.Contains("synthetic-exec-1", fixture);
        Assert.DoesNotContain("DROP TABLE", fixture,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TRUNCATE", fixture,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RehearsalBash_SyntaxIsValidOnLinux()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Root(), "deploy", "deribit-evidence",
            "rehearse-evidence-schema.pg17.sh");
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = "bash",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        proc.StartInfo.ArgumentList.Add("-n");
        proc.StartInfo.ArgumentList.Add(path);
        Assert.True(proc.Start());
        if (!proc.WaitForExit(15000))
        {
            proc.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Bash syntax check timeout.");
        }
        Assert.True(proc.ExitCode == 0,
            "Bash syntax failure: " + proc.StandardError.ReadToEnd());
    }
}
