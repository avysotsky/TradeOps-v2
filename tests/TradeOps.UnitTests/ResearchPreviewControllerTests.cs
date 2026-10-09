using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using TradeOps.Api.Controllers;
using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.TranscriptResearchDemo;
using TradeOps.Domain.Enums;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class ResearchPreviewControllerTests
{
    private static ResearchPreviewController Controller(bool enabled = true) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ResearchPreview:Enabled"] = enabled.ToString(),
            ["OperatorApi:Authentication:Enabled"] = "true"
        }).Build());

    private static ResearchDecision Decision() => new(
        "sample-decision", "sample-strategy",
        new InstrumentReference("SAMP", AssetClass.Stock, "USD"),
        ResearchDecisionAction.SetTargetWeight,
        new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero),
        TargetWeight: 0.30m,
        Confidence: 0.90m);

    private static RebalancePreviewHttpRequest Request() => new(
        Decision(),
        new PortfolioSnapshot("USD", 10000m, 10000m,
            Array.Empty<PortfolioPosition>(),
            new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)),
        100m, null);

    [Fact]
    public void ResearchValidationUsesProductionValidator()
    {
        var response = Controller().Decision(Decision());
        var result = Assert.IsType<OkObjectResult>(response);
        var validation = Assert.IsType<ResearchDecisionValidationResult>(result.Value);
        Assert.True(validation.IsValid);
    }

    [Fact]
    public void RebalanceRouteUsesProductionPlanner()
    {
        var response = Controller().Rebalance(Request());
        var result = Assert.IsType<OkObjectResult>(response);
        var plan = Assert.IsType<RebalancePlan>(result.Value);
        Assert.Equal(RebalancePlanStatus.Ready, plan.Status);
        Assert.Equal(30m, plan.OrderIntent!.Quantity);
        Assert.Equal("sample-decision", plan.DecisionId);
    }

    [Fact]
    public void RebalanceInvalidInputIsRejected()
    {
        var response = Controller().Rebalance(Request() with { ReferencePrice = -1m });
        var result = Assert.IsType<OkObjectResult>(response);
        var plan = Assert.IsType<RebalancePlan>(result.Value);
        Assert.Equal(RebalancePlanStatus.Blocked, plan.Status);
    }

    [Fact]
    public void AllRoutesAreDisabledWithoutExplicitOptIn()
    {
        Assert.IsType<NotFoundResult>(Controller(false).Decision(Decision()));
        Assert.IsType<NotFoundResult>(Controller(false).Rebalance(Request()));
    }
    [Fact]
    public void DerivationFromDocFlowArtifactsMatchesApplicationService()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TradeOps.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        var sample = Path.Combine(root.FullName, "samples", "research", "transcript-research");
        var manifest = TranscriptResearchDemoManifestLoader.Load(Path.Combine(sample, "manifest.json"));
        var policy = EarningsResearchPolicyConfiguration.Load(
            File.ReadAllText(Path.Combine(sample, "policy.json")));
        Assert.True(policy.IsValid);
        var request = new TranscriptResearchDecisionDemoRequest(
            new TranscriptResearchArtifactBundle(
                File.ReadAllText(manifest.Prior.NormalizedDocumentPath),
                File.ReadAllText(manifest.Prior.StructuredExtractionPath),
                manifest.Prior.Context),
            new TranscriptResearchArtifactBundle(
                File.ReadAllText(manifest.Current.NormalizedDocumentPath),
                File.ReadAllText(manifest.Current.StructuredExtractionPath),
                manifest.Current.Context),
            policy.Definition!);
        var expected = new TranscriptResearchDecisionDemoService().Run(request);
        var response = Controller().Derive(request);
        var body = Assert.IsType<OkObjectResult>(response).Value;
        Assert.NotNull(body);
        var decision = (ResearchDecision)body.GetType().GetProperty("Decision")!.GetValue(body)!;
        Assert.Equal(expected.Decision.DecisionId, decision.DecisionId);
        Assert.Equal(expected.Decision.Action, decision.Action);
        Assert.Equal(expected.Decision.TargetWeight, decision.TargetWeight);
        Assert.Equal(expected.Decision.GeneratedAt, decision.GeneratedAt);
        Assert.Equal(expected.Decision.Instrument, decision.Instrument);
        Assert.Equal(expected.Decision.Metadata?.OrderBy(x => x.Key),
            decision.Metadata?.OrderBy(x => x.Key));
    }

    [Fact]
    public void MalformedResearchArtifactsFailClosed()
    {
        var result = Controller().Derive(null);
        Assert.IsType<BadRequestObjectResult>(result);
    }

}
