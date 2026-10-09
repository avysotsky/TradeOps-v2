using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TradeOps.SignedWebhookDemo;

internal static class Program
{
    private const string SignalPath = "/api/signals";
    private const string SignalApiKeyHeader = "X-TradeOps-Api-Key";
    private const string TimestampHeader = "X-TradeOps-Timestamp";
    private const string RequestIdHeader = "X-TradeOps-Request-Id";
    private const string SignatureHeader = "X-TradeOps-Signature";
    private const string OperatorApiKeyHeader = "X-TradeOps-Operator-Key";

    public static async Task<int> Main()
    {
        try
        {
            var apiUrl = ReadOptional(
                "TRADEOPS_DEMO_API_URL",
                "http://localhost:8080");
            var signalApiKey = RequireEnvironmentVariable(
                "TRADEOPS_DEMO_SIGNAL_API_KEY");
            var signingSecret = RequireEnvironmentVariable(
                "TRADEOPS_DEMO_SIGNING_SECRET");
            var operatorApiKey = RequireEnvironmentVariable(
                "TRADEOPS_DEMO_OPERATOR_API_KEY");

            using var httpClient = new HttpClient
            {
                BaseAddress = new Uri(apiUrl, UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(15)
            };

            await VerifyReadinessAsync(httpClient);

            var signalId = Guid.NewGuid();
            var requestId = Guid.NewGuid();
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                symbol = "BTCUSDT",
                side = "Buy",
                quantity = 0.001m,
                source = "signed-webhook-e2e-demo",
                signalId
            });

            var tamperedBody = JsonSerializer.SerializeToUtf8Bytes(new
            {
                symbol = "BTCUSDT",
                side = "Buy",
                quantity = 0.002m,
                source = "signed-webhook-e2e-demo-tampered",
                signalId = Guid.NewGuid()
            });

            var signature = ComputeSignature(
                signingSecret,
                timestamp,
                requestId,
                HttpMethod.Post.Method,
                SignalPath,
                body);

            Console.WriteLine("TradeOps signed webhook end-to-end demo");
            Console.WriteLine($"API: {apiUrl}");
            Console.WriteLine($"SignalId: {signalId:D}");
            Console.WriteLine($"RequestId: {requestId:D}");
            Console.WriteLine();

            Console.WriteLine("1) Tamper check: send modified body with the original signature.");
            using (var tamperedResponse = await SendSignedSignalAsync(
                       httpClient,
                       signalApiKey,
                       timestamp,
                       requestId,
                       signature,
                       tamperedBody))
            {
                RequireStatus(
                    tamperedResponse,
                    HttpStatusCode.Unauthorized,
                    "Tampered body must fail HMAC verification.");
            }
            Console.WriteLine("   PASS: tampered body rejected with HTTP 401.");

            Console.WriteLine("2) Send the correctly signed request with the same request ID.");
            using (var acceptedResponse = await SendSignedSignalAsync(
                       httpClient,
                       signalApiKey,
                       timestamp,
                       requestId,
                       signature,
                       body))
            {
                RequireStatus(
                    acceptedResponse,
                    HttpStatusCode.OK,
                    "Correctly signed signal must be accepted.");

                using var acceptedJson = await ReadJsonAsync(acceptedResponse);
                var order = acceptedJson.RootElement.GetProperty("order");
                var status = order.GetProperty("status").GetString();
                var filled = order.GetProperty("filledQuantity").GetDecimal();

                if (!string.Equals(
                        status,
                        "PartiallyFilled",
                        StringComparison.Ordinal)
                    || filled != 0.0006m)
                {
                    throw new InvalidOperationException(
                        $"Unexpected mock execution state: status={status}, filled={filled}.");
                }
            }
            Console.WriteLine("   PASS: signed request accepted; mock order is PartiallyFilled at 60%.");

            Console.WriteLine("3) Replay the exact same signed request.");
            using (var replayResponse = await SendSignedSignalAsync(
                       httpClient,
                       signalApiKey,
                       timestamp,
                       requestId,
                       signature,
                       body))
            {
                RequireStatus(
                    replayResponse,
                    HttpStatusCode.Conflict,
                    "Replay must be rejected by the persistent request-id ledger.");
            }
            Console.WriteLine("   PASS: replay rejected with HTTP 409.");

            Console.WriteLine("4) Read request-level ingress audit and correlation.");
            using var ingressAuditResponse = await httpClient.GetAsync(
                $"/api/signal-ingress/requests/{requestId:D}");
            RequireStatus(
                ingressAuditResponse,
                HttpStatusCode.OK,
                "Ingress request audit must be available.");

            using var ingressAuditJson = await ReadJsonAsync(
                ingressAuditResponse);
            var ingressAttempts = ingressAuditJson.RootElement;

            if (ingressAttempts.GetArrayLength() != 3)
            {
                throw new InvalidOperationException(
                    $"Expected 3 ingress attempts, got {ingressAttempts.GetArrayLength()}.");
            }

            var firstOutcome = ingressAttempts[0]
                .GetProperty("outcome")
                .GetString();
            var secondOutcome = ingressAttempts[1]
                .GetProperty("outcome")
                .GetString();
            var thirdOutcome = ingressAttempts[2]
                .GetProperty("outcome")
                .GetString();

            if (!string.Equals(
                    firstOutcome,
                    "SignatureRejected",
                    StringComparison.Ordinal)
                || !string.Equals(
                    secondOutcome,
                    "Accepted",
                    StringComparison.Ordinal)
                || !string.Equals(
                    thirdOutcome,
                    "ReplayRejected",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Unexpected ingress outcomes: {firstOutcome}, {secondOutcome}, {thirdOutcome}.");
            }

            var correlatedSignalId = ingressAttempts[1]
                .GetProperty("signalId")
                .GetGuid();
            var correlatedOrderId = ingressAttempts[1]
                .GetProperty("orderId")
                .GetGuid();
            var correlatedClientOrderId = ingressAttempts[1]
                .GetProperty("clientOrderId")
                .GetString();

            if (correlatedSignalId != signalId
                || correlatedOrderId == Guid.Empty
                || string.IsNullOrWhiteSpace(correlatedClientOrderId))
            {
                throw new InvalidOperationException(
                    "Accepted ingress audit is missing signal/order correlation.");
            }

            Console.WriteLine(
                $"   PASS: outcomes={firstOutcome}->{secondOutcome}->{thirdOutcome}; orderId={correlatedOrderId:D}.");

            Console.WriteLine("5) Read signal audit and deterministic client order ID.");
            using var signalAuditResponse = await GetAsOperatorAsync(
                httpClient,
                operatorApiKey,
                $"/api/signals/{signalId:D}");
            RequireStatus(
                signalAuditResponse,
                HttpStatusCode.OK,
                "Signal audit must be available.");

            using var signalAuditJson = await ReadJsonAsync(signalAuditResponse);
            var signalAudit = signalAuditJson.RootElement;
            var outcome = signalAudit.GetProperty("outcome").GetString();
            var clientOrderId = signalAudit
                .GetProperty("clientOrderId")
                .GetString();

            if (!string.Equals(outcome, "Accepted", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(clientOrderId))
            {
                throw new InvalidOperationException(
                    "Signal audit does not contain an accepted outcome and client order ID.");
            }
            Console.WriteLine($"   PASS: signal audit outcome={outcome}; clientOrderId={clientOrderId}.");

            Console.WriteLine("6) Reconcile through the separately protected operator API.");
            using (var reconcileRequest = new HttpRequestMessage(
                       HttpMethod.Post,
                       "/api/system/reconcile"))
            {
                reconcileRequest.Headers.Add(
                    OperatorApiKeyHeader,
                    operatorApiKey);

                using var reconcileResponse = await httpClient.SendAsync(
                    reconcileRequest);
                RequireStatus(
                    reconcileResponse,
                    HttpStatusCode.OK,
                    "Operator-authenticated reconciliation must succeed.");

                using var reconcileJson = await ReadJsonAsync(reconcileResponse);
                var issues = reconcileJson.RootElement.GetProperty("issues");
                if (issues.GetArrayLength() != 0)
                {
                    throw new InvalidOperationException(
                        "Mock reconciliation reported unexpected issues.");
                }
            }
            Console.WriteLine("   PASS: operator-authenticated reconciliation completed.");

            Console.WriteLine("7) Verify local lifecycle ends in Filled.");
            using var historyResponse = await GetAsOperatorAsync(
                httpClient,
                operatorApiKey,
                $"/api/orders/local/{Uri.EscapeDataString(clientOrderId)}/history");
            RequireStatus(
                historyResponse,
                HttpStatusCode.OK,
                "Local order history must be available.");

            using var historyJson = await ReadJsonAsync(historyResponse);
            var history = historyJson.RootElement;
            if (history.GetArrayLength() == 0)
            {
                throw new InvalidOperationException("Order lifecycle history is empty.");
            }

            var finalStatus = history[history.GetArrayLength() - 1]
                .GetProperty("status")
                .GetString();

            if (!string.Equals(finalStatus, "Filled", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Expected final order state Filled, got {finalStatus}.");
            }
            Console.WriteLine("   PASS: lifecycle audit ends in Filled.");

            Console.WriteLine("8) Verify execution metrics are queryable.");
            using var metricsResponse = await httpClient.GetAsync(
                "/api/metrics/execution");
            RequireStatus(
                metricsResponse,
                HttpStatusCode.OK,
                "Execution metrics must be available.");

            using var metricsJson = await ReadJsonAsync(metricsResponse);
            var metrics = metricsJson.RootElement;
            var receivedSignals = metrics
                .GetProperty("signals")
                .GetProperty("received")
                .GetInt32();

            if (receivedSignals < 1)
            {
                throw new InvalidOperationException(
                    "Execution metrics do not include the accepted signal.");
            }
            Console.WriteLine($"   PASS: execution metrics report {receivedSignals} received signal(s).");

            Console.WriteLine();
            Console.WriteLine("SIGNED WEBHOOK E2E DEMO: PASS");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"SIGNED WEBHOOK E2E DEMO: FAIL - {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static async Task<HttpResponseMessage> GetAsOperatorAsync(
        HttpClient httpClient,
        string operatorApiKey,
        string path)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            path);
        request.Headers.Add(
            OperatorApiKeyHeader,
            operatorApiKey);
        return await httpClient.SendAsync(request);
    }

    private static async Task VerifyReadinessAsync(HttpClient httpClient)
    {
        using var response = await httpClient.GetAsync("/health/ready");
        RequireStatus(
            response,
            HttpStatusCode.OK,
            "TradeOps API is not ready.");
    }

    private static async Task<HttpResponseMessage> SendSignedSignalAsync(
        HttpClient httpClient,
        string signalApiKey,
        long timestamp,
        Guid requestId,
        string signature,
        byte[] body)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            SignalPath);

        request.Headers.Add(SignalApiKeyHeader, signalApiKey);
        request.Headers.Add(
            TimestampHeader,
            timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(
            RequestIdHeader,
            requestId.ToString("D"));
        request.Headers.Add(SignatureHeader, signature);

        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/json");

        return await httpClient.SendAsync(request);
    }

    private static string ComputeSignature(
        string signingSecret,
        long timestamp,
        Guid requestId,
        string method,
        string path,
        ReadOnlySpan<byte> body)
    {
        var prefix = string.Join(
            "\n",
            timestamp.ToString(CultureInfo.InvariantCulture),
            requestId.ToString("D"),
            method.ToUpperInvariant(),
            path,
            string.Empty);

        var prefixBytes = Encoding.UTF8.GetBytes(prefix);
        var payload = new byte[prefixBytes.Length + body.Length];

        prefixBytes.CopyTo(payload, 0);
        body.CopyTo(payload.AsSpan(prefixBytes.Length));

        var hash = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(signingSecret),
            payload);

        return $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
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

    private static string ReadOptional(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim();
    }

    private static string RequireEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Required environment variable {name} is not set.");
        }

        return value.Trim();
    }
}
