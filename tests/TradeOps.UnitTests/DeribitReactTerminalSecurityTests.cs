using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using TradeOps.Api.Controllers;
using Xunit;

namespace TradeOps.UnitTests;

/// <summary>
/// Verifies manual Deribit trading remains fail-closed before any HTTP exchange
/// request or persistent database access. Does not use live credentials.
/// </summary>
public sealed class DeribitReactTerminalSecurityTests
{
    private sealed class NoNetworkFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("No exchange HTTP calls allowed by this offline test.");
    }

    private static DeribitTerminalController Controller(
        bool execution = false, bool mainnet = false, bool mainnetOverride = false,
        string operatorKey = "This-local-only-key-is-long-enough-for-testing")
    {
        var dictionary = new Dictionary<string, string?>
        {
            ["DeribitTerminal:Environment"] = mainnet ? "mainnet" : "testnet",
            ["DeribitTerminal:ExecutionEnabled"] = execution.ToString(),
            ["DeribitTerminal:MainnetExecutionEnabled"] = mainnetOverride.ToString(),
            ["DeribitTerminal:OperatorKey"] = operatorKey,
            ["DeribitTerminal:ClientId"] = "offline-fixture-id",
            ["DeribitTerminal:ClientSecret"] = "offline-fixture-secret",
            ["DeribitTerminal:MaxContracts"] = "100"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(dictionary).Build();
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers["X-TradeOps-Terminal-Key"] = operatorKey;
        return new DeribitTerminalController(new NoNetworkFactory(), configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    [Fact]
    public void DefaultExecutionDisabled_FailsClosedBeforeAnyExchangeRequest()
    {
        var controller = Controller();
        var result = controller.PreviewOrder(
            new DeribitTerminalController.PreviewRequest("BTC-PERPETUAL", "buy", 10m, 65000m));
        Assert.IsType<ConflictObjectResult>(result);

        var status = Assert.IsType<OkObjectResult>(controller.Status());
        var json = JsonSerializer.Serialize(status.Value);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("tradingEnabled").GetBoolean());
        Assert.Equal("testnet", doc.RootElement.GetProperty("environment").GetString());
    }

    [Fact]
    public void MainnetRequiresTwoIndependentServerFeatureFlags()
    {
        var ctrl = Controller(execution: true, mainnet: true);
        Assert.IsType<ConflictObjectResult>(ctrl.PreviewOrder(
            new DeribitTerminalController.PreviewRequest("BTC-PERPETUAL", "buy", 10m, 65000m)));

        var approved = Controller(execution: true, mainnet: true, mainnetOverride: true);
        var result = Assert.IsType<OkObjectResult>(approved.PreviewOrder(
            new DeribitTerminalController.PreviewRequest("BTC-PERPETUAL", "sell", 10m, 66000m)));
        var json = JsonSerializer.Serialize(result.Value);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("postOnly").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("rejectPostOnly").GetBoolean());
        Assert.Equal("mainnet", doc.RootElement.GetProperty("environment").GetString());
    }

    [Theory]
    [InlineData("BTC-PERPETUAL", "buy", 0, 65000)]
    [InlineData("BTC-PERPETUAL", "sell", 101, 65000)]
    [InlineData("DOGE-PERPETUAL", "buy", 1, 65000)]
    [InlineData("BTC-PERPETUAL", "BUY", 1, 65000)]
    [InlineData("BTC-PERPETUAL", "buy", 10, 0)]
    public void InvalidOrderPreviewRejectedWithoutExchangeNetwork(
        string symbol, string side, int quantity, int price)
    {
        var controller = Controller(execution: true);
        Assert.IsType<BadRequestObjectResult>(controller.PreviewOrder(
            new DeribitTerminalController.PreviewRequest(symbol, side, quantity, price)));
    }

    [Fact]
    public void RefusesNonLoopbackClientsAndInvalidOperatorKey()
    {
        var controller = Controller(execution: true);
        controller.ControllerContext.HttpContext.Connection.RemoteIpAddress =
            IPAddress.Parse("192.168.1.21");
        Assert.IsType<UnauthorizedObjectResult>(controller.Status());

        var local = Controller();
        local.ControllerContext.HttpContext.Request.Headers["X-TradeOps-Terminal-Key"] =
            "incorrect-operator-key";
        Assert.IsType<UnauthorizedObjectResult>(local.Status());
    }

    [Fact]
    public async Task ConfirmWithoutPreviewAndCancelWithoutExactPhraseRejected()
    {
        var controller = Controller(execution: true);
        var result = await controller.ConfirmOrder(
            new DeribitTerminalController.ConfirmRequest(Guid.NewGuid()), default);
        Assert.IsType<ConflictObjectResult>(result);

        var cancel = await controller.Cancel(
            new DeribitTerminalController.CancelRequest("EXCHANGE_ID", "YES"), default);
        Assert.IsType<BadRequestObjectResult>(cancel);
    }

    [Fact]
    public void ReadOnlyMainnetLauncher_UsesExistingDpapiAndForcesLiveExecutionOff()
    {
        var code = File.ReadAllText(Path.Combine(LocateRoot(),
            "scripts/deribit/Start-MainnetReactTerminalReadOnly.ps1"));
        Assert.Contains("Deribit.Mainnet.ReadOnly.dpapi.json", code);
        Assert.Contains("DeribitMainnetReadOnly", code);
        Assert.Contains("rev-parse FETCH_HEAD", code);
        Assert.Contains("$actual -cne $ExpectedHead", code);
        Assert.Contains("worktree add --detach", code);
        Assert.Contains("& $dotnet build $project -c Release", code);
        Assert.Contains("ConvertTo-SecureString", code);
        Assert.Contains("$env:DeribitTerminal__Environment = 'mainnet'", code);
        Assert.Contains("$env:DeribitTerminal__ExecutionEnabled = 'false'", code);
        Assert.Contains("$env:DeribitTerminal__MainnetExecutionEnabled = 'false'", code);
        Assert.DoesNotContain("Read-Host", code);
        Assert.Contains("New-Object byte[] 48", code);
        Assert.Contains("RandomNumberGenerator", code);
        Assert.Contains("TRADEOPS_TERMINAL_OPERATOR_KEY", code);
        Assert.Contains("Start-Process -FilePath $nodeFound.Source", code);
        Assert.Contains("TRADEOPS_REACT_TERMINAL_MAINNET_READONLY_STARTED", code);
        Assert.DoesNotContain("DeribitTerminal__ExecutionEnabled = 'true'", code);
        Assert.DoesNotContain("DeribitTerminal__MainnetExecutionEnabled = 'true'", code);
        Assert.DoesNotContain("Write-Host $id", code);
        Assert.DoesNotContain("Write-Host $secret", code);
    }

    [Fact]
    public void TerminalUsesPostOnlyNoAutotradingAndLocalViteProxy()
    {
        var root = LocateRoot();
        var server = File.ReadAllText(Path.Combine(root,
            "apps/TradeOps.TerminalApi/Controllers/DeribitTerminalController.cs"));
        var frontend = File.ReadAllText(Path.Combine(root,
            "apps/trade-terminal/src/Terminal.tsx"));
        var vite = File.ReadAllText(Path.Combine(root,
            "apps/trade-terminal/vite.config.ts"));

        Assert.Contains("post_only", server);
        Assert.Contains("reject_post_only", server);
        Assert.Contains("Pending.TryRemove(request.PreviewId", server);
        Assert.Contains("MainnetExecutionEnabled", server);
        Assert.Contains("IPAddress.IsLoopback", server);
        Assert.Contains("Crypto", server, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private/cancel_all", server);
        Assert.DoesNotContain("private/withdraw", server);
        Assert.DoesNotContain("window.localStorage", frontend);
        Assert.DoesNotContain("sessionStorage", frontend);
        Assert.Contains("Проверить заявку", frontend);
        Assert.Contains("Разместить РЕАЛЬНЫЙ ордер", frontend);
        Assert.Contains("127.0.0.1", vite);
        Assert.Contains("readonlyOperatorKey", vite);
        Assert.Contains("request.method === 'GET'", vite);
        Assert.Contains("proxyReq.setHeader('X-TradeOps-Terminal-Key'", vite);
        Assert.Contains("void connect();", frontend);
        Assert.DoesNotContain("type=\"password\"", frontend);
        Assert.DoesNotContain("setTypedKey(", frontend);
    }

    private static string LocateRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TradeOps.sln"))) return d.FullName;
        throw new InvalidOperationException("TradeOps root not found.");
    }
}
