using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace TradeOps.UnitTests;

/// <summary>
/// Tests the real operator-invoked acceptance script. The independent Linux
/// WSL2 unit-test runner must reject malformed Bash before any Windows owner
/// is asked to start disposable PostgreSQL or restore dependencies.
/// </summary>
public sealed class TradeOpsRealPostgresAcceptanceScriptTests
{
    private static string ScriptPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TradeOps.sln")))
            {
                var script = Path.Combine(
                    directory.FullName, "scripts", "ibkr",
                    "to-v2-14-run-real-postgres-acceptance.sh");
                Assert.True(File.Exists(script), "Acceptance Bash script is missing.");
                return script;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "The repository root was not found; acceptance script cannot be qualified.");
    }

    [Fact]
    public void RealPostgresAcceptanceScript_ParsesWithBash()
    {
        var script = ScriptPath();

        // Linux runner qualification is REQUIRED, not a string-level guess.
        // On Windows without Git Bash, syntax is checked by the Windows
        // launcher via WSL2 before invoking the actual acceptance script.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "bash",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        process.StartInfo.ArgumentList.Add("-n");
        process.StartInfo.ArgumentList.Add(script);

        Assert.True(process.Start(), "Could not start Bash syntax validation.");
        if (!process.WaitForExit(15000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException(
                "Bash acceptance syntax validation exceeded 15 seconds.");
        }

        var error = process.StandardError.ReadToEnd();
        Assert.True(process.ExitCode == 0,
            $"Acceptance Bash syntax invalid (exit {process.ExitCode}): {error}");
    }

    [Fact]
    public void MessagePackDependency_UsesPatchedVersion_AndKeepsNuGetAuditEnabled()
    {
        var root = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(ScriptPath())!, "..", ".."));
        var projectPath = Path.Combine(root, "src", "TradeOps.Infrastructure",
            "TradeOps.Infrastructure.csproj");

        var project = System.Xml.Linq.XDocument.Load(projectPath);
        var package = project.Descendants("PackageReference").Single(
            element => (string?)element.Attribute("Include") == "MessagePack");
        var rawVersion = (string?)package.Attribute("Version");
        Assert.True(Version.TryParse(rawVersion, out var version),
            "MessagePack version must be an exact, non-floating version.");
        Assert.True(version >= new Version(3, 1, 9),
            "MessagePack must include the GHSA-qhrr-8q5h-9q3h patch.");

        // Acceptance cannot be made to PASS by hiding NuGet security findings.
        var script = File.ReadAllText(ScriptPath());
        Assert.DoesNotContain("--ignore-failed-sources", script, StringComparison.Ordinal);
        Assert.DoesNotContain("NuGetAudit=false", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NU1902", script, StringComparison.Ordinal);
    }

    [Fact]
    public void RealPostgresAcceptanceScript_IsStructurallyCompleteAndNotDuplicated()
    {
        var source = File.ReadAllText(ScriptPath());
        Assert.Equal(1, Regex.Matches(source,
            """^start_api "api-first"$""", RegexOptions.Multiline).Count);
        Assert.Equal(1, Regex.Matches(source,
            "^completed=1$", RegexOptions.Multiline).Count);
        Assert.Equal(1, Regex.Matches(source,
            """^worker_is_running\(\) \{$""", RegexOptions.Multiline).Count);
        Assert.Contains(
            "IBKR OFFLINE POSTGRES SMOKE PASS cases=[0-9]+ broker_requests=0 cleanup=true$",
            source, StringComparison.Ordinal);
        Assert.Contains(
            "journal_smoke_success_marker_missing",
            source, StringComparison.Ordinal);
        Assert.Contains("stage=temporary_cache_cleanup",
            source, StringComparison.Ordinal);
        Assert.False(source.Contains((char)13), "Acceptance script contains CRLF.");
    }
}
