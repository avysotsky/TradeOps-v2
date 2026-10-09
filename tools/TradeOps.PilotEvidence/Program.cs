using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace TradeOps.PilotEvidence;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> Main()
    {
        try
        {
            var apiUrl = Optional("TRADEOPS_PILOT_API_URL", "http://localhost:8080");
            var eventId = Required("TRADEOPS_PILOT_EVENT_ID");
            var outputDir = Optional("TRADEOPS_PILOT_OUTPUT_DIR", "pilot-evidence");
            var client = Optional("TRADEOPS_PILOT_CLIENT", "Not specified");
            var stage = Optional("TRADEOPS_PILOT_STAGE", "Mock");
            var version = Optional("TRADEOPS_PILOT_VERSION", "v1.3.2.1");

            using var http = new HttpClient
            {
                BaseAddress = new Uri(apiUrl, UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(15)
            };

            var operatorKey = Environment.GetEnvironmentVariable(
                "TRADEOPS_OPERATOR_API_KEY");
            if (!string.IsNullOrWhiteSpace(operatorKey))
            {
                var operatorHeader = Optional(
                    "TRADEOPS_OPERATOR_API_HEADER",
                    "X-TradeOps-Operator-Key");
                http.DefaultRequestHeaders.Add(
                    operatorHeader,
                    operatorKey.Trim());
            }

            await RequireOk(http, "/health/ready", "TradeOps API is not ready.");

            var deliveries = await Get<Delivery[]>(
                http,
                "/api/integrations/tradingview/operations/deliveries?eventId="
                    + Uri.EscapeDataString(eventId)
                    + "&limit=200");

            if (deliveries.Length == 0)
                throw new InvalidOperationException("No deliveries found for eventId.");

            deliveries = deliveries
                .OrderBy(x => x.ReceivedAt)
                .ThenBy(x => x.DeliveryId)
                .ToArray();

            var signalIds = deliveries
                .Where(x => x.SignalId.HasValue)
                .Select(x => x.SignalId!.Value)
                .Distinct()
                .ToArray();

            var clientOrderIds = deliveries
                .Where(x => !string.IsNullOrWhiteSpace(x.ClientOrderId))
                .Select(x => x.ClientOrderId!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (signalIds.Length != 1 || clientOrderIds.Length != 1)
                throw new InvalidOperationException(
                    "Delivery audit does not resolve to one canonical signal/order identity.");

            var signalId = signalIds[0];
            var clientOrderId = clientOrderIds[0];

            var signal = await Get<Signal>(
                http,
                "/api/signals/" + signalId.ToString("D"));

            var order = await Get<LocalOrder>(
                http,
                "/api/orders/local/" + Uri.EscapeDataString(clientOrderId));

            var lifecycle = await Get<LifecycleEvent[]>(
                http,
                "/api/orders/local/"
                    + Uri.EscapeDataString(clientOrderId)
                    + "/history");

            var from = deliveries.Min(x => x.ReceivedAt).AddMinutes(-5);
            var to = deliveries
                .Select(x => x.CompletedAt ?? x.ReceivedAt)
                .Max()
                .AddMinutes(5);

            var now = DateTimeOffset.UtcNow;
            if (to > now) to = now;
            if (to <= from) to = from.AddMinutes(10);

            var metrics = await Get<DeliveryMetrics>(
                http,
                "/api/integrations/tradingview/operations/metrics?from="
                    + Uri.EscapeDataString(from.ToString("O"))
                    + "&to="
                    + Uri.EscapeDataString(to.ToString("O")));

            var health = await GetOptional<DeliveryHealth>(
                http,
                "/api/integrations/tradingview/operations/health");

            var reconciliation = await GetOptional<ReconciliationStatus>(
                http,
                "/api/system/reconciliation/status");

            var checks = Evaluate(
                deliveries,
                signal,
                order,
                lifecycle,
                metrics,
                health,
                reconciliation,
                signalId,
                clientOrderId);

            var evidence = new Evidence(
                "1.0",
                DateTimeOffset.UtcNow,
                version,
                client,
                stage,
                apiUrl,
                eventId,
                checks.All(x => x.Passed) ? "PASS" : "FAIL",
                new Correlation(
                    eventId,
                    signalId,
                    clientOrderId,
                    order.Id,
                    order.ExchangeOrderId),
                deliveries,
                signal,
                order,
                lifecycle.OrderBy(x => x.OccurredAt).ToArray(),
                metrics,
                health,
                reconciliation,
                checks);

            Directory.CreateDirectory(outputDir);
            var jsonPath = Path.Combine(outputDir, "pilot-evidence.json");
            var mdPath = Path.Combine(outputDir, "pilot-evidence.md");

            await File.WriteAllTextAsync(
                jsonPath,
                JsonSerializer.Serialize(evidence, JsonOptions)
                    + Environment.NewLine);
            await File.WriteAllTextAsync(mdPath, Markdown(evidence));

            Console.WriteLine("PILOT EVIDENCE: " + evidence.TechnicalDecision);
            Console.WriteLine("JSON: " + jsonPath);
            Console.WriteLine("Markdown: " + mdPath);
            Console.WriteLine(
                "Correlation: eventId="
                + eventId
                + " -> signalId="
                + signalId.ToString("D")
                + " -> clientOrderId="
                + clientOrderId);

            return evidence.TechnicalDecision == "PASS" ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "PILOT EVIDENCE: ERROR - "
                + ex.GetType().Name
                + ": "
                + ex.Message);
            return 1;
        }
    }

    private static IReadOnlyCollection<CheckResult> Evaluate(
        IReadOnlyCollection<Delivery> deliveries,
        Signal signal,
        LocalOrder order,
        IReadOnlyCollection<LifecycleEvent> lifecycle,
        DeliveryMetrics metrics,
        DeliveryHealth? health,
        ReconciliationStatus? reconciliation,
        Guid signalId,
        string clientOrderId)
    {
        var outcomes = deliveries
            .Select(x => x.Outcome)
            .ToHashSet(StringComparer.Ordinal);

        var correlated = deliveries.All(
            x => x.SignalId == signalId
                && string.Equals(
                    x.ClientOrderId,
                    clientOrderId,
                    StringComparison.Ordinal));

        var history = lifecycle.OrderBy(x => x.OccurredAt).ToArray();

        return new[]
        {
            Check("Accepted delivery",
                outcomes.Contains("Accepted"),
                "At least one authenticated delivery is Accepted."),
            Check("Idempotent redelivery",
                outcomes.Contains("Redelivered"),
                "Exact redelivery is recorded as Redelivered."),
            Check("Conflict protection",
                outcomes.Contains("Conflict"),
                "Conflicting reuse of eventId is recorded as Conflict."),
            Check("Canonical correlation",
                correlated
                && signal.SignalId == signalId
                && string.Equals(signal.ClientOrderId, clientOrderId, StringComparison.Ordinal)
                && signal.OrderId == order.Id,
                "All delivery attempts resolve to one SignalId and ClientOrderId."),
            Check("Lifecycle evidence",
                history.Length > 0
                && history.All(x =>
                    x.OrderId == order.Id
                    && string.Equals(x.ClientOrderId, clientOrderId, StringComparison.Ordinal)),
                history.Length == 0
                    ? "No lifecycle events were returned."
                    : "Observed "
                        + history.Length
                        + " lifecycle events; latest="
                        + history[^1].Status
                        + "."),
            Check("Reconciliation evidence",
                reconciliation is { IsRunning: false, Succeeded: true },
                reconciliation is null
                    ? "No reconciliation status is available."
                    : "Succeeded="
                        + reconciliation.Succeeded
                        + ", issues="
                        + reconciliation.OrderIssues
                        + ", missing="
                        + reconciliation.OrdersMissingOnExchange
                        + "."),
            Check("Delivery metrics",
                metrics.Total >= deliveries.Count
                && metrics.Accepted >= 1
                && metrics.Redelivered >= 1
                && metrics.Conflict >= 1,
                "total="
                    + metrics.Total
                    + ", accepted="
                    + metrics.Accepted
                    + ", redelivered="
                    + metrics.Redelivered
                    + ", conflict="
                    + metrics.Conflict
                    + "."),
            Check("Delivery health snapshot",
                health is not null
                && !string.Equals(health.Status, "Critical", StringComparison.Ordinal),
                health is null
                    ? "No persisted health snapshot is available."
                    : "Status=" + health.Status + "; " + health.Reason)
        };
    }

    private static CheckResult Check(string name, bool passed, string detail) =>
        new(name, passed, detail);

    private static string Markdown(Evidence e)
    {
        var b = new StringBuilder();
        b.AppendLine("# TradeOps Paid Pilot Evidence");
        b.AppendLine();
        b.AppendLine("- **Technical decision:** " + e.TechnicalDecision);
        b.AppendLine("- **Generated at:** " + e.GeneratedAt.ToString("O"));
        b.AppendLine("- **TradeOps version:** " + e.TradeOpsVersion);
        b.AppendLine("- **Client:** " + Clean(e.Client));
        b.AppendLine("- **Stage:** " + Clean(e.Stage));
        b.AppendLine("- **Event ID:** " + e.EventId);
        b.AppendLine();
        b.AppendLine("> Execution/integration evidence only. This report does not certify alpha, strategy quality, profitability, return, or mainnet readiness.");
        b.AppendLine();
        b.AppendLine("## Correlation");
        b.AppendLine();
        b.AppendLine("    eventId       = " + e.Correlation.EventId);
        b.AppendLine("    SignalId      = " + e.Correlation.SignalId.ToString("D"));
        b.AppendLine("    ClientOrderId = " + e.Correlation.ClientOrderId);
        b.AppendLine("    LocalOrderId  = " + e.Correlation.LocalOrderId.ToString("D"));
        b.AppendLine("    ExchangeOrder = " + (e.Correlation.ExchangeOrderId ?? "n/a"));
        b.AppendLine();
        b.AppendLine("## Technical acceptance checks");
        b.AppendLine();
        b.AppendLine("| Check | Result | Evidence |");
        b.AppendLine("| --- | --- | --- |");
        foreach (var c in e.Checks)
            b.AppendLine("| " + Table(c.Name) + " | " + (c.Passed ? "PASS" : "FAIL") + " | " + Table(c.Detail) + " |");

        b.AppendLine();
        b.AppendLine("## TradingView deliveries");
        b.AppendLine();
        b.AppendLine("| Received | Delivery ID | Outcome | HTTP | Latency ms |");
        b.AppendLine("| --- | --- | --- | ---: | ---: |");
        foreach (var d in e.Deliveries)
            b.AppendLine("| "
                + d.ReceivedAt.ToString("O")
                + " | "
                + d.DeliveryId.ToString("D")
                + " | "
                + d.Outcome
                + " | "
                + (d.HttpStatusCode?.ToString() ?? "n/a")
                + " | "
                + (d.DurationMilliseconds?.ToString() ?? "n/a")
                + " |");

        b.AppendLine();
        b.AppendLine("## Signal and order");
        b.AppendLine();
        b.AppendLine("- Signal outcome: **" + e.Signal.Outcome + "**");
        b.AppendLine("- Symbol / side: **" + e.Signal.Symbol + " / " + e.Signal.Side + "**");
        b.AppendLine("- Requested quantity: **" + e.Signal.RequestedQuantity + "**");
        b.AppendLine("- Local order status: **" + e.Order.Status + "**");
        b.AppendLine("- Filled quantity: **" + e.Order.FilledQuantity + "**");
        b.AppendLine("- Average fill price: **" + (e.Order.AverageFillPrice?.ToString() ?? "n/a") + "**");

        b.AppendLine();
        b.AppendLine("## Lifecycle");
        b.AppendLine();
        b.AppendLine("| Time | Previous | Status | Filled quantity | Source |");
        b.AppendLine("| --- | --- | --- | ---: | --- |");
        foreach (var h in e.Lifecycle)
            b.AppendLine("| "
                + h.OccurredAt.ToString("O")
                + " | "
                + (h.PreviousStatus ?? "n/a")
                + " | "
                + h.Status
                + " | "
                + h.FilledQuantity
                + " | "
                + Table(h.Source)
                + " |");

        b.AppendLine();
        b.AppendLine("## Operations snapshot");
        b.AppendLine();
        b.AppendLine("- Delivery totals: total="
            + e.DeliveryMetrics.Total
            + ", accepted="
            + e.DeliveryMetrics.Accepted
            + ", redelivered="
            + e.DeliveryMetrics.Redelivered
            + ", conflict="
            + e.DeliveryMetrics.Conflict
            + ", failed="
            + e.DeliveryMetrics.Failed);
        b.AppendLine("- Average latency: "
            + (e.DeliveryMetrics.AverageLatencyMilliseconds?.ToString("F0") ?? "n/a")
            + " ms");
        b.AppendLine("- Health: **" + (e.DeliveryHealth?.Status ?? "not available") + "**");
        b.AppendLine("- Reconciliation: **"
            + (e.Reconciliation?.Succeeded == true ? "Succeeded" : "not confirmed")
            + "**");

        b.AppendLine();
        b.AppendLine("## Client sign-off");
        b.AppendLine();
        b.AppendLine("    Accepted by client:");
        b.AppendLine("    Client representative:");
        b.AppendLine("    Date:");
        b.AppendLine("    Open issues:");
        return b.ToString();
    }

    private static async Task RequireOk(
        HttpClient http,
        string path,
        string message)
    {
        using var response = await http.GetAsync(path);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException(
                message + " HTTP " + (int)response.StatusCode + ".");
    }

    private static async Task<T> Get<T>(
        HttpClient http,
        string path)
    {
        using var response = await http.GetAsync(path);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException(
                "Evidence endpoint "
                + path
                + " returned HTTP "
                + (int)response.StatusCode
                + ".");

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions)
            ?? throw new InvalidOperationException("Expected JSON response.");
    }

    private static async Task<T?> GetOptional<T>(
        HttpClient http,
        string path)
        where T : class
    {
        using var response = await http.GetAsync(path);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException(
                "Evidence endpoint "
                + path
                + " returned HTTP "
                + (int)response.StatusCode
                + ".");

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions);
    }

    private static string Optional(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string Required(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                "Required environment variable " + name + " is not set.");
        return value.Trim();
    }

    private static string Clean(string value) =>
        value.Replace((char)13, ' ').Replace((char)10, ' ');

    private static string Table(string value) =>
        Clean(value).Replace("|", "&#124;", StringComparison.Ordinal);

    private sealed record Evidence(
        string SchemaVersion,
        DateTimeOffset GeneratedAt,
        string TradeOpsVersion,
        string Client,
        string Stage,
        string ApiUrl,
        string EventId,
        string TechnicalDecision,
        Correlation Correlation,
        IReadOnlyCollection<Delivery> Deliveries,
        Signal Signal,
        LocalOrder Order,
        IReadOnlyCollection<LifecycleEvent> Lifecycle,
        DeliveryMetrics DeliveryMetrics,
        DeliveryHealth? DeliveryHealth,
        ReconciliationStatus? Reconciliation,
        IReadOnlyCollection<CheckResult> Checks);

    private sealed record CheckResult(string Name, bool Passed, string Detail);
    private sealed record Correlation(
        string EventId,
        Guid SignalId,
        string ClientOrderId,
        Guid LocalOrderId,
        string? ExchangeOrderId);

    private sealed record Delivery(
        Guid DeliveryId,
        string? EventId,
        DateTimeOffset ReceivedAt,
        DateTimeOffset? CompletedAt,
        long? DurationMilliseconds,
        string Outcome,
        int? HttpStatusCode,
        Guid? SignalId,
        Guid? OrderId,
        string? ClientOrderId,
        string? Symbol,
        string? Action);

    private sealed record Signal(
        Guid SignalId,
        string Symbol,
        string Side,
        string? SignalType,
        decimal RequestedQuantity,
        decimal? RiskPercent,
        decimal? StopLoss,
        decimal? TakeProfit,
        DateTimeOffset ReceivedAt,
        string? Source,
        string Outcome,
        IReadOnlyCollection<string> RiskRejectionReasons,
        Guid? OrderId,
        string? ClientOrderId,
        string? ExecutionIssueCode,
        string? ExecutionIssueMessage,
        DateTimeOffset? ExecutionIssueAt);

    private sealed record LocalOrder(
        Guid Id,
        string? ExchangeOrderId,
        string ClientOrderId,
        string Symbol,
        string Side,
        string OrderType,
        decimal RequestedQuantity,
        decimal FilledQuantity,
        decimal? AverageFillPrice,
        decimal? Price,
        string Status,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private sealed record LifecycleEvent(
        Guid Id,
        Guid OrderId,
        string ClientOrderId,
        string? PreviousStatus,
        string Status,
        decimal FilledQuantity,
        decimal? AverageFillPrice,
        string? ExchangeOrderId,
        string Source,
        DateTimeOffset OccurredAt);

    private sealed record DeliveryMetrics(
        DateTimeOffset GeneratedAt,
        DateTimeOffset FromInclusive,
        DateTimeOffset ToExclusive,
        int Total,
        int Pending,
        int Accepted,
        int Redelivered,
        int ValidationRejected,
        int RiskRejected,
        int Conflict,
        int Failed,
        double? AverageLatencyMilliseconds,
        long? MaxLatencyMilliseconds,
        DateTimeOffset? LatestDeliveryAt,
        DateTimeOffset? LatestSuccessfulAt);

    private sealed record DeliveryHealth(
        string Status,
        string Reason,
        DateTimeOffset UpdatedAt,
        DateTimeOffset WindowFrom,
        DateTimeOffset WindowTo,
        int Total,
        int Failed,
        int Conflict,
        int RiskRejected,
        double? AverageLatencyMilliseconds,
        DateTimeOffset? LatestDeliveryAt,
        DateTimeOffset? LatestSuccessfulAt);

    private sealed record ReconciliationStatus(
        string RunType,
        DateTimeOffset StartedAt,
        DateTimeOffset? CompletedAt,
        bool IsRunning,
        bool? Succeeded,
        int OrdersScanned,
        int OrdersUpdated,
        int OrderIssues,
        int OrdersMissingOnExchange,
        int PositionsCompared,
        int PositionMismatches,
        int PositionSnapshots,
        string? ErrorMessage,
        DateTimeOffset UpdatedAt);
}
