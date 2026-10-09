using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TradeOps.TradingViewDemo;

internal static class Program
{
    private const string TradingViewPath =
        "/api/integrations/tradingview";
    private const string GatewayHeader =
        "X-TradeOps-TradingView-Gateway-Key";
    private const string OperatorHeader =
        "X-TradeOps-Operator-Key";
    private const string DeliveryHeader =
        "X-TradeOps-TradingView-Delivery-Id";

    public static async Task<int> Main()
    {
        try
        {
            var apiUrl = ReadOptional(
                "TRADEOPS_TRADINGVIEW_DEMO_API_URL",
                "http://localhost:8080");
            var gatewayKey = RequireEnvironmentVariable(
                "TRADEOPS_TRADINGVIEW_DEMO_GATEWAY_KEY");
            var operatorKey = RequireEnvironmentVariable(
                "TRADEOPS_TRADINGVIEW_DEMO_OPERATOR_API_KEY");

            using var httpClient = new HttpClient
            {
                BaseAddress = new Uri(apiUrl, UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(15)
            };

            await VerifyReadinessAsync(httpClient);

            var eventId = ReadOptional(
                "TRADEOPS_TRADINGVIEW_DEMO_EVENT_ID",
                $"customer-demo|BTCUSDT|{Guid.NewGuid():N}");

            var firstPayload = new
            {
                eventId,
                symbol = "BTCUSDT",
                action = "buy",
                quantity = 0.001m,
                riskPercent = 1.0m
            };

            Console.WriteLine(
                "TradeOps customer TradingView demo");
            Console.WriteLine($"API: {apiUrl}");
            Console.WriteLine($"EventId: {eventId}");
            Console.WriteLine();

            Console.WriteLine(
                "1) Send a TradingView-style event.");
            TradingViewResponse first;
            string firstDeliveryId;

            using (var response = await SendTradingViewAsync(
                       httpClient,
                       gatewayKey,
                       firstPayload))
            {
                RequireStatus(
                    response,
                    HttpStatusCode.OK,
                    "First TradingView delivery must be accepted.");

                firstDeliveryId = RequireHeader(
                    response,
                    DeliveryHeader);

                first = await ReadJsonAsync<TradingViewResponse>(
                    response);

                if (!first.Accepted
                    || first.SignalId == Guid.Empty
                    || first.Order is null
                    || string.IsNullOrWhiteSpace(
                        first.Order.ClientOrderId))
                {
                    throw new InvalidOperationException(
                        "Accepted delivery is missing signal/order correlation.");
                }
            }

            Console.WriteLine(
                $"   PASS: deliveryId={firstDeliveryId}, signalId={first.SignalId:D}, clientOrderId={first.Order!.ClientOrderId}.");

            Console.WriteLine(
                "2) Redeliver the exact same TradingView event.");

            TradingViewResponse retry;
            string retryDeliveryId;

            using (var response = await SendTradingViewAsync(
                       httpClient,
                       gatewayKey,
                       firstPayload))
            {
                RequireStatus(
                    response,
                    HttpStatusCode.OK,
                    "Exact provider redelivery must remain idempotent.");

                retryDeliveryId = RequireHeader(
                    response,
                    DeliveryHeader);

                retry = await ReadJsonAsync<TradingViewResponse>(
                    response);
            }

            if (retry.SignalId != first.SignalId
                || retry.Order?.ClientOrderId
                    != first.Order.ClientOrderId)
            {
                throw new InvalidOperationException(
                    "Provider redelivery created a different signal/order identity.");
            }

            Console.WriteLine(
                $"   PASS: deliveryId={retryDeliveryId}; same SignalId and ClientOrderId returned.");

            Console.WriteLine(
                "3) Reuse the same eventId with conflicting execution fields.");

            var conflictingPayload = new
            {
                eventId,
                symbol = "BTCUSDT",
                action = "buy",
                quantity = 0.002m,
                riskPercent = 1.0m
            };

            string conflictDeliveryId;
            using (var response = await SendTradingViewAsync(
                       httpClient,
                       gatewayKey,
                       conflictingPayload))
            {
                RequireStatus(
                    response,
                    HttpStatusCode.Conflict,
                    "Conflicting event-id reuse must be rejected.");

                conflictDeliveryId = RequireHeader(
                    response,
                    DeliveryHeader);
            }

            Console.WriteLine(
                $"   PASS: deliveryId={conflictDeliveryId}; conflicting reuse rejected with HTTP 409.");

            Console.WriteLine(
                "4) Read provider delivery audit.");

            using var deliveriesResponse = await GetAsOperatorAsync(
                httpClient,
                operatorKey,
                $"/api/integrations/tradingview/operations/deliveries?eventId={Uri.EscapeDataString(eventId)}&limit=10");
            RequireStatus(
                deliveriesResponse,
                HttpStatusCode.OK,
                "TradingView delivery audit must be queryable.");

            var deliveries =
                await ReadJsonAsync<TradingViewDelivery[]>(
                    deliveriesResponse);

            if (deliveries.Length != 3)
            {
                throw new InvalidOperationException(
                    $"Expected 3 provider deliveries, got {deliveries.Length}.");
            }

            var outcomes = deliveries
                .Select(item => item.Outcome)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var expected in new[]
                     {
                         "Accepted",
                         "Redelivered",
                         "Conflict"
                     })
            {
                if (!outcomes.Contains(expected))
                {
                    throw new InvalidOperationException(
                        $"Provider audit is missing outcome '{expected}'.");
                }
            }

            if (deliveries.Count(
                    item => item.SignalId == first.SignalId)
                != 3)
            {
                throw new InvalidOperationException(
                    "Provider deliveries are not consistently correlated to the canonical signal.");
            }

            Console.WriteLine(
                "   PASS: Accepted -> Redelivered -> Conflict is visible in persistent delivery audit.");

            Console.WriteLine(
                "5) Reconcile through the protected operator API.");

            using (var request = new HttpRequestMessage(
                       HttpMethod.Post,
                       "/api/system/reconcile"))
            {
                request.Headers.Add(
                    OperatorHeader,
                    operatorKey);

                using var response = await httpClient.SendAsync(
                    request);

                RequireStatus(
                    response,
                    HttpStatusCode.OK,
                    "Operator-authenticated reconciliation must succeed.");
            }

            Console.WriteLine(
                "   PASS: reconciliation completed.");

            Console.WriteLine(
                "6) Verify order lifecycle reaches Filled.");

            using var historyResponse = await GetAsOperatorAsync(
                httpClient,
                operatorKey,
                $"/api/orders/local/{Uri.EscapeDataString(first.Order.ClientOrderId)}/history");

            RequireStatus(
                historyResponse,
                HttpStatusCode.OK,
                "Local lifecycle history must be queryable.");

            var history = await ReadJsonAsync<OrderHistory[]>(
                historyResponse);

            if (history.Length == 0
                || !string.Equals(
                    history[^1].Status,
                    "Filled",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Mock order did not reach Filled after reconciliation.");
            }

            Console.WriteLine(
                "   PASS: persisted lifecycle ends in Filled.");

            Console.WriteLine(
                "7) Read TradingView delivery metrics.");

            using var metricsResponse = await GetAsOperatorAsync(
                httpClient,
                operatorKey,
                "/api/integrations/tradingview/operations/metrics");

            RequireStatus(
                metricsResponse,
                HttpStatusCode.OK,
                "TradingView delivery metrics must be queryable.");

            var metrics =
                await ReadJsonAsync<TradingViewMetrics>(
                    metricsResponse);

            if (metrics.Total < 3
                || metrics.Accepted < 1
                || metrics.Redelivered < 1
                || metrics.Conflict < 1)
            {
                throw new InvalidOperationException(
                    "TradingView metrics do not reflect the demo deliveries.");
            }

            Console.WriteLine(
                $"   PASS: metrics total={metrics.Total}, accepted={metrics.Accepted}, redelivered={metrics.Redelivered}, conflict={metrics.Conflict}.");

            Console.WriteLine(
                "8) Wait for Worker health evaluation.");

            var health = await WaitForHealthyAsync(
                httpClient,
                operatorKey,
                TimeSpan.FromSeconds(40));

            Console.WriteLine(
                $"   PASS: delivery health={health.Status}; total={health.Total}; averageLatencyMs={health.AverageLatencyMilliseconds?.ToString("F0") ?? "n/a"}.");

            Console.WriteLine();
            Console.WriteLine(
                "CUSTOMER TRADINGVIEW DEMO: PASS");
            Console.WriteLine(
                $"Correlation: eventId={eventId} -> signalId={first.SignalId:D} -> clientOrderId={first.Order.ClientOrderId}");

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"CUSTOMER TRADINGVIEW DEMO: FAIL - {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static async Task<HttpResponseMessage>
        SendTradingViewAsync(
            HttpClient httpClient,
            string gatewayKey,
            object payload)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            TradingViewPath);

        request.Headers.Add(
            GatewayHeader,
            gatewayKey);
        request.Content = JsonContent.Create(payload);

        return await httpClient.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetAsOperatorAsync(
        HttpClient httpClient,
        string operatorKey,
        string path)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            path);
        request.Headers.Add(
            OperatorHeader,
            operatorKey);
        return await httpClient.SendAsync(request);
    }

    private static async Task VerifyReadinessAsync(
        HttpClient httpClient)
    {
        using var response = await httpClient.GetAsync(
            "/health/ready");

        RequireStatus(
            response,
            HttpStatusCode.OK,
            "TradeOps API is not ready.");
    }

    private static async Task<TradingViewHealth>
        WaitForHealthyAsync(
            HttpClient httpClient,
            string operatorKey,
            TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        TradingViewHealth? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await GetAsOperatorAsync(
                httpClient,
                operatorKey,
                "/api/integrations/tradingview/operations/health");

            if (response.StatusCode == HttpStatusCode.OK)
            {
                last = await ReadJsonAsync<TradingViewHealth>(
                    response);

                if (string.Equals(
                        last.Status,
                        "Healthy",
                        StringComparison.Ordinal))
                {
                    return last;
                }
            }
            else if (response.StatusCode
                     != HttpStatusCode.NotFound)
            {
                RequireStatus(
                    response,
                    HttpStatusCode.OK,
                    "TradingView health endpoint returned an unexpected status.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new InvalidOperationException(
            $"TradingView health did not become Healthy within {timeout.TotalSeconds:F0}s. Last status={last?.Status ?? "not-evaluated"}.");
    }

    private static async Task<T> ReadJsonAsync<T>(
        HttpResponseMessage response)
    {
        var value = await response.Content
            .ReadFromJsonAsync<T>();

        return value
            ?? throw new InvalidOperationException(
                "Expected JSON response body.");
    }

    private static string RequireHeader(
        HttpResponseMessage response,
        string name)
    {
        if (!response.Headers.TryGetValues(
                name,
                out var values))
        {
            throw new InvalidOperationException(
                $"Response header '{name}' is missing.");
        }

        var value = values.SingleOrDefault();

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Response header '{name}' is empty.");
        }

        return value;
    }

    private static void RequireStatus(
        HttpResponseMessage response,
        HttpStatusCode expected,
        string message)
    {
        if (response.StatusCode != expected)
        {
            throw new InvalidOperationException(
                $"{message} Expected {(int)expected}, got {(int)response.StatusCode}.");
        }
    }

    private static string ReadOptional(
        string name,
        string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);

        return string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim();
    }

    private static string RequireEnvironmentVariable(
        string name)
    {
        var value = Environment.GetEnvironmentVariable(name);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Required environment variable {name} is not set.");
        }

        return value.Trim();
    }

    private sealed record TradingViewResponse(
        string EventId,
        Guid SignalId,
        bool Accepted,
        IReadOnlyCollection<string> RiskReasons,
        OrderResponse? Order);

    private sealed record OrderResponse(
        string? ExchangeOrderId,
        string ClientOrderId,
        string Status,
        decimal FilledQuantity,
        decimal? AverageFillPrice);

    private sealed record TradingViewDelivery(
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

    private sealed record OrderHistory(
        string Status,
        decimal FilledQuantity);

    private sealed record TradingViewMetrics(
        int Total,
        int Accepted,
        int Redelivered,
        int Conflict);

    private sealed record TradingViewHealth(
        string Status,
        string Reason,
        int Total,
        double? AverageLatencyMilliseconds);
}
