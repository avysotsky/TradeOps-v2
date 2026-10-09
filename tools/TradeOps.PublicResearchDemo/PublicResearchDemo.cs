using System.Text.Json;
using System.Text.Json.Serialization;
using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Enums;

namespace TradeOps.PublicResearchDemo;

public sealed record PublicResearchDemoPolicy(
    int SchemaVersion,
    string StrategyId,
    string Fingerprint,
    EarningsDecisionRuleSettings DecisionRuleSettings,
    EarningsTargetWeightPolicy TargetWeightPolicy);

public sealed record PublicResearchDemoPolicyLoadResult(
    PublicResearchDemoPolicy? Policy,
    IReadOnlyList<EarningsResearchPolicyValidationError> Errors)
{
    public bool IsValid =>
        Policy is not null &&
        Errors.Count == 0;
}

public sealed record PublicDemoPolicyOutput(
    int SchemaVersion,
    string StrategyId,
    string Fingerprint);

public sealed record PublicDemoComposition(
    PublicResearchDemoPolicy Policy,
    ResearchToRebalanceDemoResult Result,
    IReadOnlyList<EarningsEvent> EarningsEvents,
    IReadOnlyList<MarketDataBar> MarketDataBars,
    IReadOnlyList<string> AccessionNumbers,
    DateOnly MarketDataStart,
    DateOnly MarketDataEnd);

public sealed record PublicDemoBacktestOutput(
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    int EventCount,
    decimal InitialEquity,
    decimal FinalEquity,
    decimal TotalReturn,
    decimal? Cagr,
    decimal MaxDrawdown,
    decimal? Sharpe,
    decimal? Sortino,
    decimal Turnover);

public sealed record PublicDemoRebalanceOutput(
    string PortfolioSource,
    decimal CurrentPortfolioWeight,
    decimal ProposedTargetWeight,
    string? Side,
    decimal? Quantity,
    decimal? EstimatedNotional,
    string Status);

public sealed record PublicDemoOutput(
    PublicDemoPolicyOutput Policy,
    string Instrument,
    string EventId,
    string AccessionNumber,
    string FiscalPeriod,
    DateTimeOffset PublishedAt,
    string Assessment,
    decimal TargetWeight,
    PublicDemoBacktestOutput Backtest,
    PublicDemoRebalanceOutput CurrentRebalance,
    string ResearchSource,
    string MarketDataSource,
    string Disclaimer);

public static class PublicResearchDemoComposer
{
    public const decimal DemoInitialNav =
        10_000m;
    public const decimal DemoPositionQuantity =
        10m;

    public static PublicDemoComposition Compose(
        PublicDemoRawSnapshot snapshot,
        PublicResearchDemoPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(policy);

        var submissionFilings =
            SecSubmissionParser.Parse(
                    snapshot.SecSubmissionsJson)
                .Where(
                    filing =>
                        string.Equals(
                            filing.FormType,
                            "10-Q",
                            StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(
                    filing =>
                        filing.AcceptedAt)
                .Take(2)
                .OrderBy(
                    filing =>
                        filing.AcceptedAt)
                .ToArray();

        if (submissionFilings.Length < 2)
        {
            throw new InvalidOperationException(
                "IBM public-data demo requires at least two historical SEC 10-Q filings.");
        }

        var structuredFilings =
            SecCompanyFactsParser
                .CreateStructuredFilings(
                    snapshot.SecCompanyFactsJson,
                    submissionFilings,
                    "IBM")
                .OrderBy(
                    filing =>
                        filing.AcceptedAt)
                .ToArray();

        var earningsEvents =
            structuredFilings
                .Select(
                    filing =>
                    {
                        var facts =
                            SecStructuredFilingNormalizer
                                .Normalize(filing);

                        return SecEarningsEventFactory.Create(
                            facts,
                            snapshot.RetrievedAt);
                    })
                .OrderBy(
                    item =>
                        item.PublishedAt)
                .ToArray();

        var instrument =
            new InstrumentReference(
                "IBM",
                AssetClass.Stock,
                "USD",
                Exchange: "NYSE");

        var allBars =
            AlphaVantageDailyParser.Parse(
                snapshot.AlphaVantageDailyJson,
                instrument);

        if (allBars.Count == 0)
        {
            throw new InvalidOperationException(
                "Alpha Vantage IBM daily series is empty.");
        }

        var currentEvent =
            earningsEvents[^1];

        var backtestStartDate =
            DateOnly.FromDateTime(
                    currentEvent.PublishedAt.UtcDateTime)
                .AddDays(-10);

        var bars =
            allBars
                .Where(
                    bar =>
                        DateOnly.FromDateTime(
                            bar.OpenTime.UtcDateTime) >=
                        backtestStartDate)
                .ToArray();

        if (bars.Length == 0)
        {
            throw new InvalidOperationException(
                "IBM market data does not cover the latest comparable SEC event.");
        }

        var decisionAvailability =
            currentEvent.PublishedAt;

        if (!bars.Any(
                bar =>
                    bar.OpenTime >
                    decisionAvailability))
        {
            throw new InvalidOperationException(
                "IBM market data has no regular-session open after the latest SEC event became available.");
        }

        var latestBar =
            allBars[^1];
        var currentPositionNotional =
            DemoPositionQuantity *
            latestBar.Close;

        if (currentPositionNotional >=
            DemoInitialNav)
        {
            throw new InvalidOperationException(
                "Demo portfolio fixture would require non-positive cash at the latest IBM price.");
        }

        var currentPortfolio =
            new PortfolioSnapshot(
                "USD",
                NetAssetValue:
                    DemoInitialNav,
                Cash:
                    DemoInitialNav -
                    currentPositionNotional,
                Positions:
                    new[]
                    {
                        new PortfolioPosition(
                            instrument,
                            DemoPositionQuantity)
                    },
                AsOf:
                    latestBar.CloseTime);

        var request =
            new ResearchToRebalanceDemoRequest(
                earningsEvents,
                bars,
                policy.DecisionRuleSettings,
                policy.TargetWeightPolicy,
                InitialCash:
                    DemoInitialNav,
                CurrentPortfolio:
                    currentPortfolio,
                CurrentReferencePrice:
                    latestBar.Close,
                BacktestConstraints:
                    new RebalanceConstraints(
                        MaxTargetWeight: 0.50m),
                CurrentRebalanceConstraints:
                    new RebalanceConstraints(
                        MaxTargetWeight: 0.50m),
                StrategyId:
                    policy.StrategyId,
                DecisionTimingMode:
                    ResearchDecisionTimingMode
                        .HistoricalPublishedAvailability);

        var result =
            new ResearchToRebalanceDemoService()
                .Run(request);

        return new PublicDemoComposition(
            policy,
            result,
            earningsEvents,
            bars,
            structuredFilings
                .Select(
                    filing =>
                        filing.AccessionNumber)
                .ToArray(),
            DateOnly.FromDateTime(
                allBars[0].OpenTime.UtcDateTime),
            DateOnly.FromDateTime(
                allBars[^1].OpenTime.UtcDateTime));
    }

    public static PublicDemoOutput ToOutput(
        PublicDemoComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);

        var result =
            composition.Result;
        var accession =
            result.LatestDecision.Metadata is not null
            && result.LatestDecision.Metadata.TryGetValue(
                "sourceDocumentId",
                out var sourceDocumentId)
                ? sourceDocumentId
                : composition.AccessionNumbers[^1];

        return new PublicDemoOutput(
            Policy:
                new PublicDemoPolicyOutput(
                    composition.Policy.SchemaVersion,
                    composition.Policy.StrategyId,
                    composition.Policy.Fingerprint),
            Instrument:
                result.Instrument.Symbol,
            EventId:
                result.EventId,
            AccessionNumber:
                accession,
            FiscalPeriod:
                result.FiscalPeriod,
            PublishedAt:
                result.PublishedAt,
            Assessment:
                result.Assessment.ToString(),
            TargetWeight:
                result.TargetWeight,
            Backtest:
                new PublicDemoBacktestOutput(
                    result.BacktestPeriodStart,
                    result.BacktestPeriodEnd,
                    result.EventCount,
                    DemoInitialNav,
                    result.FinalEquity,
                    result.TotalReturn,
                    result.Cagr,
                    result.MaxDrawdown,
                    result.Sharpe,
                    result.Sortino,
                    result.Turnover),
            CurrentRebalance:
                new PublicDemoRebalanceOutput(
                    PortfolioSource:
                        "demo portfolio snapshot",
                    CurrentPortfolioWeight:
                        result.CurrentPortfolioWeight,
                    ProposedTargetWeight:
                        result.ProposedTargetWeight,
                    Side:
                        result.RebalanceSide?.ToString(),
                    Quantity:
                        result.RebalanceQuantity,
                    EstimatedNotional:
                        result.RebalanceNotional,
                    Status:
                        result.RebalanceStatus.ToString()),
            ResearchSource:
                "SEC EDGAR / XBRL public APIs",
            MarketDataSource:
                "Alpha Vantage TIME_SERIES_DAILY public demo endpoint for IBM",
            Disclaimer:
                "Software pipeline demonstration only; historical output is not a forecast or evidence of future returns.");
    }
}

public static class PublicResearchDemoCli
{
    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            PropertyNamingPolicy =
                JsonNamingPolicy.CamelCase,
            WriteIndented =
                true,
            Converters =
            {
                new JsonStringEnumConverter()
            }
        };

    public static async Task<int> RunAsync(
        string[] args)
    {
        var options =
            ParseArgs(args);

        var policyLoad =
            LoadPolicyFromFile(
                options.PolicyPath);

        if (!policyLoad.IsValid)
        {
            Console.Error.WriteLine(
                FormatPolicyValidationFailure(
                    policyLoad.Errors));
            return 2;
        }

        var policy =
            policyLoad.Policy!;

        Console.WriteLine(
            "POLICY VALIDATION: PASS");

        PublicDemoRawSnapshot snapshot;
        PublicDemoCacheManifest? manifest =
            null;

        if (!options.Refresh
            && PublicDemoCache.Exists(
                options.CacheRoot))
        {
            (
                snapshot,
                manifest) =
                PublicDemoCache.Load(
                    options.CacheRoot);

            Console.WriteLine(
                $"Input: verified local cache ({options.CacheRoot})");
        }
        else
        {
            using var downloader =
                new PublicDemoDownloader();

            snapshot =
                await downloader.DownloadIbmAsync();

            Console.WriteLine(
                "Input: opt-in public network acquisition");
        }

        var composition =
            PublicResearchDemoComposer.Compose(
                snapshot,
                policy);

        if (manifest is null)
        {
            manifest =
                PublicDemoCache.Save(
                    options.CacheRoot,
                    snapshot,
                    composition.AccessionNumbers,
                    composition.MarketDataStart,
                    composition.MarketDataEnd);

            Console.WriteLine(
                $"Cache: {options.CacheRoot}");
        }

        var output =
            PublicResearchDemoComposer.ToOutput(
                composition);

        var jsonPath =
            options.JsonPath
            ?? Path.Combine(
                options.CacheRoot,
                "ibm-result.json");

        var jsonDirectory =
            Path.GetDirectoryName(
                Path.GetFullPath(
                    jsonPath));

        if (!string.IsNullOrWhiteSpace(
                jsonDirectory))
        {
            Directory.CreateDirectory(
                jsonDirectory);
        }

        File.WriteAllText(
            jsonPath,
            JsonSerializer.Serialize(
                output,
                JsonOptions));

        Print(output);

        Console.WriteLine(
            $"JSON artifact: {jsonPath}");
        Console.WriteLine(
            $"Snapshot retrievedAt: {manifest.RetrievedAt:O}");

        return 0;
    }

    private static void Print(
        PublicDemoOutput output)
    {
        Console.WriteLine();
        Console.WriteLine(
            "TradeOps public-data demo — IBM");
        Console.WriteLine(
            "Policy:");
        Console.WriteLine(
            $"  SchemaVersion: {output.Policy.SchemaVersion}");
        Console.WriteLine(
            $"  StrategyId: {output.Policy.StrategyId}");
        Console.WriteLine(
            $"  Fingerprint: {output.Policy.Fingerprint}");
        Console.WriteLine();
        Console.WriteLine(
            "Research result:");
        Console.WriteLine(
            $"Instrument: {output.Instrument}");
        Console.WriteLine(
            $"SEC event: {output.EventId}");
        Console.WriteLine(
            $"Accession: {output.AccessionNumber}");
        Console.WriteLine(
            $"Fiscal period: {output.FiscalPeriod}");
        Console.WriteLine(
            $"PublishedAt: {output.PublishedAt:O}");
        Console.WriteLine(
            $"Assessment: {output.Assessment}");
        Console.WriteLine(
            $"Target weight: {output.TargetWeight:P2}");
        Console.WriteLine();
        Console.WriteLine(
            "Backtest:");
        Console.WriteLine(
            $"  period: {output.Backtest.PeriodStart:O} .. {output.Backtest.PeriodEnd:O}");
        Console.WriteLine(
            $"  event count: {output.Backtest.EventCount}");
        Console.WriteLine(
            $"  initial equity: {output.Backtest.InitialEquity:0.00} USD");
        Console.WriteLine(
            $"  final equity: {output.Backtest.FinalEquity:0.00} USD");
        Console.WriteLine(
            $"  TotalReturn: {output.Backtest.TotalReturn:P4}");
        Console.WriteLine(
            $"  CAGR: {FormatNullablePercent(output.Backtest.Cagr)}");
        Console.WriteLine(
            $"  MaxDrawdown: {output.Backtest.MaxDrawdown:P4}");
        Console.WriteLine(
            $"  Sharpe: {FormatNullable(output.Backtest.Sharpe)}");
        Console.WriteLine(
            $"  Sortino: {FormatNullable(output.Backtest.Sortino)}");
        Console.WriteLine(
            $"  Turnover: {output.Backtest.Turnover:0.######}");
        Console.WriteLine();
        Console.WriteLine(
            "Current rebalance example:");
        Console.WriteLine(
            $"  portfolio source: {output.CurrentRebalance.PortfolioSource}");
        Console.WriteLine(
            $"  current portfolio weight: {output.CurrentRebalance.CurrentPortfolioWeight:P2}");
        Console.WriteLine(
            $"  proposed target weight: {output.CurrentRebalance.ProposedTargetWeight:P2}");
        Console.WriteLine(
            $"  side: {output.CurrentRebalance.Side ?? "n/a"}");
        Console.WriteLine(
            $"  quantity: {FormatNullable(output.CurrentRebalance.Quantity)}");
        Console.WriteLine(
            $"  estimated notional: {FormatNullable(output.CurrentRebalance.EstimatedNotional)} USD");
        Console.WriteLine(
            $"  status: {output.CurrentRebalance.Status}");
        Console.WriteLine();
        Console.WriteLine(
            output.Disclaimer);
    }

    private static string FormatNullable(
        decimal? value) =>
        value.HasValue
            ? value.Value.ToString(
                "0.######")
            : "n/a";

    private static string FormatNullablePercent(
        decimal? value) =>
        value.HasValue
            ? value.Value.ToString(
                "P4")
            : "n/a";

    private static CliOptions ParseArgs(
        string[] args)
    {
        var refresh =
            false;
        var cacheRoot =
            PublicDemoCache.DefaultRoot;
        string? jsonPath =
            null;
        string? policyPath =
            null;

        for (var index = 0;
             index < args.Length;
             index++)
        {
            switch (args[index])
            {
                case "--refresh":
                    refresh = true;
                    break;

                case "--cache":
                    cacheRoot =
                        RequireValue(
                            args,
                            ref index,
                            "--cache");
                    break;

                case "--json":
                    jsonPath =
                        RequireValue(
                            args,
                            ref index,
                            "--json");
                    break;

                case "--policy":
                    policyPath =
                        RequireValue(
                            args,
                            ref index,
                            "--policy");
                    break;

                default:
                    throw new ArgumentException(
                        $"Unknown argument '{args[index]}'. Supported: --policy <path>, --refresh, --cache <path>, --json <path>.");
            }
        }

        return new CliOptions(
            refresh,
            cacheRoot,
            jsonPath,
            policyPath);
    }

    public static PublicResearchDemoPolicyLoadResult LoadPolicyFromFile(
        string? policyPath)
    {
        if (string.IsNullOrWhiteSpace(
                policyPath))
        {
            return PolicyFailure(
                "policy_required",
                "$.policy",
                "Client-ready execution requires --policy <path>.");
        }

        if (!File.Exists(
                policyPath))
        {
            return PolicyFailure(
                "policy_file_not_found",
                "$.policy",
                $"Policy file was not found: {policyPath}");
        }

        string json;

        try
        {
            json =
                File.ReadAllText(
                    policyPath);
        }
        catch (Exception exception)
            when (exception is IOException
                  or UnauthorizedAccessException)
        {
            return PolicyFailure(
                "policy_file_read_failed",
                "$.policy",
                $"Policy file could not be read: {exception.Message}");
        }

        var loaded =
            EarningsResearchPolicyConfiguration.Load(
                json);

        if (!loaded.IsValid
            || loaded.Definition is null
            || string.IsNullOrWhiteSpace(
                loaded.Fingerprint))
        {
            return new PublicResearchDemoPolicyLoadResult(
                null,
                loaded.Validation.Errors);
        }

        var definition =
            loaded.Definition;

        return new PublicResearchDemoPolicyLoadResult(
            new PublicResearchDemoPolicy(
                definition.SchemaVersion,
                definition.StrategyId,
                loaded.Fingerprint,
                EarningsResearchPolicyConfiguration
                    .ToDecisionRuleSettings(
                        definition),
                EarningsResearchPolicyConfiguration
                    .ToTargetWeightPolicy(
                        definition)),
            Array.Empty<EarningsResearchPolicyValidationError>());
    }

    public static string FormatPolicyValidationFailure(
        IReadOnlyList<EarningsResearchPolicyValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var lines =
            new List<string>
            {
                "POLICY VALIDATION: FAIL"
            };

        foreach (var error in errors)
        {
            lines.Add(
                $"code={error.Code}");
            lines.Add(
                $"path={error.Path}");
            lines.Add(
                $"message={error.Message}");
        }

        return string.Join(
            Environment.NewLine,
            lines);
    }

    private static PublicResearchDemoPolicyLoadResult PolicyFailure(
        string code,
        string path,
        string message) =>
        new(
            null,
            new[]
            {
                new EarningsResearchPolicyValidationError(
                    code,
                    path,
                    message)
            });

    private static string RequireValue(
        string[] args,
        ref int index,
        string option)
    {
        if (index + 1 >= args.Length
            || string.IsNullOrWhiteSpace(
                args[index + 1]))
        {
            throw new ArgumentException(
                $"{option} requires a value.");
        }

        index++;
        return args[index];
    }

    private sealed record CliOptions(
        bool Refresh,
        string CacheRoot,
        string? JsonPath,
        string? PolicyPath);
}
