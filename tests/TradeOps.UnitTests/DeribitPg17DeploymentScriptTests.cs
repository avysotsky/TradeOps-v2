using System.Diagnostics;
using Xunit;

namespace TradeOps.UnitTests;

/// <summary>
/// Offline regression checks for an explicitly owner-run WSL PostgreSQL
/// bootstrap. Never accesses Docker or a database from unit tests.
/// </summary>
public sealed class DeribitPg17DeploymentScriptTests
{
    private static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory);
             d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "TradeOps.sln")))
                return d.FullName;
        }
        throw new InvalidOperationException("TradeOps repository root unavailable.");
    }

    [Fact]
    public void WindowsLauncher_ConvertsBackslashesAndChecksWslpathBeforeTrim()
    {
        var path = Path.Combine(Root(), "scripts", "deribit",
            "Deploy-EmptyEvidencePg17.ps1");
        var script = File.ReadAllText(path);
        Assert.Contains("$WindowsPath.Replace([char]92, [char]47)", script);
        Assert.Contains("wslpath -u -a $portablePath", script);
        Assert.Contains("$lines.Count -ne 1", script);
        Assert.Contains("[string]::IsNullOrWhiteSpace($converted)", script);
        Assert.Contains("COMPOSE_WSL_FILE_NOT_FOUND", script);
        Assert.Contains("SCRIPT_WSL_FILE_NOT_FOUND", script);
        Assert.DoesNotContain("(& wsl.exe -- wslpath -a $composeWindows).Trim()", script);
        Assert.DoesNotContain("(& wsl.exe -- wslpath -a $scriptWindows).Trim()", script);
        Assert.Contains("UNQUALIFIED_DEPLOYMENT_SHA", script);
        Assert.Contains("bash -n $scriptWsl", script);
    }

    [Fact]
    public void Bootstrap_IsExclusiveToNewNamedPostgres17AndDoesNotApplySchema()
    {
        var root = Root();
        var bash = File.ReadAllText(Path.Combine(root, "deploy",
            "deribit-evidence", "deploy-empty-pg17.sh"));
        var compose = File.ReadAllText(Path.Combine(root, "deploy",
            "deribit-evidence", "docker-compose.pg17.yml"));
        Assert.Contains("docker volume inspect \"$volume\" >/dev/null 2>&1 && fail VOLUME_ALREADY_EXISTS", bash);
        Assert.Contains("docker container inspect \"$container\" >/dev/null 2>&1 && fail CONTAINER_ALREADY_EXISTS", bash);
        Assert.Contains("TRADEOPS_PG17_EMPTY_DEPLOY_PASS", bash);
        Assert.Contains("broker_calls=0 migration_calls=0", bash);
        Assert.Contains("image: postgres:17-bookworm", compose);
        Assert.Contains("127.0.0.1:54329:5432", compose);
        Assert.Contains("tradeops-deribit-evidence-pg17-data", compose);
        Assert.DoesNotContain("docker compose down -v", bash);
        Assert.DoesNotContain("DROP DATABASE", bash, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE SCHEMA", bash, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BootstrapBash_ParsesOnLinux()
    {
        if (OperatingSystem.IsWindows())
            return; // Windows launcher checks via WSL bash -n.

        var path = Path.Combine(Root(), "deploy", "deribit-evidence",
            "deploy-empty-pg17.sh");
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
            "Bash syntax error: " + process.StandardError.ReadToEnd());
    }
}
