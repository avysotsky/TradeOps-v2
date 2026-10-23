using System.Diagnostics;
using Xunit;

namespace TradeOps.UnitTests;

/// <summary>
/// Static/offline safety checks for owner-only empty PG17 backup and restore.
/// No test starts Docker, reads live broker data or modifies PostgreSQL.
/// </summary>
public sealed class DeribitPg17BackupRestoreScriptTests
{
    private static string RepoRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "TradeOps.sln")))
                return current.FullName;
        }
        throw new InvalidOperationException("Cannot resolve TradeOps repository.");
    }

    [Fact]
    public void PinnedWindowsEntry_VerifiesShaAndWslConversion()
    {
        var code = File.ReadAllText(Path.Combine(
            RepoRoot(), "scripts", "deribit",
            "Verify-EmptyEvidencePg17Backup.ps1"));
        Assert.Contains("rev-parse FETCH_HEAD", code);
        Assert.Contains("$actual -cne $ExpectedHead", code);
        Assert.Contains("UNQUALIFIED_BACKUP_SCRIPT_SHA", code);
        Assert.Contains("git.exe -C $Repo show $sourceRef", code);
        Assert.Contains("Replace([char]92, [char]47)", code);
        Assert.Contains("$convertedLines.Count -ne 1", code);
        Assert.Contains("bash -n $wslScript", code);
        Assert.Contains("bash $wslScript", code);
        Assert.DoesNotContain("ExecutionPolicy Bypass", code);
    }

    [Fact]
    public void ReadOnlyPersistentDump_IsRestoredOnlyIntoNetworkDisabledTmpfsContainer()
    {
        var script = File.ReadAllText(Path.Combine(
            RepoRoot(), "deploy", "deribit-evidence",
            "backup-verify-empty-pg17.sh"));
        Assert.Contains("source_container='tradeops-deribit-evidence-pg17'", script);
        Assert.Contains("source_volume='tradeops-deribit-evidence-pg17-data'", script);
        Assert.Contains("source_db='tradeops_evidence'", script);
        Assert.Contains("source_identity", script);
        Assert.Contains("tradeops_evidence|17|0", script);
        Assert.Contains("pg_dump", script);
        Assert.Contains("--format=custom", script);
        Assert.Contains("pg_restore --list", script);
        Assert.Contains("--network none", script);
        Assert.Contains("--tmpfs /var/lib/postgresql/data", script);
        Assert.Contains("--env POSTGRES_DB=\"$restore_db\"", script);
        Assert.Contains("--exit-on-error --no-owner --no-acl", script);
        Assert.Contains("sha256sum", script);
        Assert.Contains("backup_saved=yes", script);
        Assert.DoesNotContain("docker volume rm", script);
        Assert.DoesNotContain("docker compose down", script);
        Assert.DoesNotContain("docker system prune", script);
        Assert.DoesNotContain("CREATE SCHEMA", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP DATABASE", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BackupRestoreBash_ParsesOnLinux()
    {
        if (OperatingSystem.IsWindows())
            return; // Owner PowerShell launcher validates with WSL bash -n.

        var path = Path.Combine(RepoRoot(), "deploy", "deribit-evidence",
            "backup-verify-empty-pg17.sh");
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
            "Bash syntax invalid: " + process.StandardError.ReadToEnd());
    }
}
