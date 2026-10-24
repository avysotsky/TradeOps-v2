using System.Diagnostics;
using Xunit;

namespace TradeOps.UnitTests;

/// <summary>
/// Static security constraints for interactive, off-WSL encrypted export.
/// Never runs GPG, reads secrets, or accesses the owner's actual recovery data.
/// </summary>
public sealed class DeribitGpgPortableRecoveryExportTests
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
    public void PowerShellEntry_PinsRemoteHeadAndNeverHandlesPassphrase()
    {
        var ps = File.ReadAllText(Path.Combine(Root(), "scripts", "deribit",
            "Export-Pg17RecoveryGpgWindowsD.ps1"));
        Assert.Contains("rev-parse FETCH_HEAD", ps);
        Assert.Contains("$head -cne $ExpectedHead", ps);
        Assert.Contains("git.exe -C $Repo show $object", ps);
        Assert.Contains("Replace([char]92, [char]47)", ps);
        Assert.Contains("wsl.exe -u user -- bash -n", ps);
        Assert.DoesNotContain("Read-Host", ps);
        Assert.DoesNotContain("ConvertFrom-SecureString", ps);
        Assert.DoesNotContain("ExecutionPolicy Bypass", ps);
    }

    [Fact]
    public void BashExport_EncryptsOnlyToWindowsDAndVerifiesRoundTrip()
    {
        var bash = File.ReadAllText(Path.Combine(Root(), "deploy", "deribit-evidence",
            "export-gpg-recovery-to-windows-d.sh"));
        Assert.Contains("role-acl-recovery-20261024T100019Z-8DoC6C", bash);
        Assert.Contains("sha256sum --check --status -- SHA256SUMS", bash);
        Assert.Contains("mountpoint -q /mnt/d", bash);
        Assert.Contains("target='/mnt/d/TradeOps-Backups'", bash);
        Assert.Contains("read -r -s pass </dev/tty", bash);
        Assert.Contains("read -r -s confirm </dev/tty", bash);
        Assert.Contains("--passphrase-fd 3", bash);
        Assert.Contains("--symmetric --cipher-algo AES256", bash);
        Assert.Contains("--s2k-mode 3 --s2k-digest-algo SHA256", bash);
        Assert.Contains("--no-symkey-cache", bash);
        Assert.Contains("--decrypt --output - \"$partial\"", bash);
        Assert.Contains("tar -xf - --no-same-owner", bash);
        Assert.Contains("cmp -s -- \"$bundle/$name\" \"$tempcheck/$name\"", bash);
        Assert.Contains("independent_physical_disk=NO", bash);
        Assert.Contains("persistent_db_writes=0", bash);
        Assert.DoesNotContain("gpg --passphrase ", bash);
        Assert.DoesNotContain("--passphrase-file", bash);
        Assert.DoesNotContain("docker ", bash);
        Assert.DoesNotContain("psql ", bash);
        Assert.DoesNotContain("curl ", bash);
    }

    [Fact]
    public void BashSyntaxIsValidOnLinux()
    {
        if (OperatingSystem.IsWindows()) return;
        var bash = Path.Combine(Root(), "deploy", "deribit-evidence",
            "export-gpg-recovery-to-windows-d.sh");
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
        process.StartInfo.ArgumentList.Add(bash);
        Assert.True(process.Start());
        if (!process.WaitForExit(15000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("GPG export Bash syntax timeout.");
        }
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }
}
