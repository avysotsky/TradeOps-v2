using System.Text.Json;

namespace TradeOps.DeribitTestnetOrderQualification;

internal static class TestnetOrderEntryPoint
{
    public static async Task<int> Main(string[] args)
    {
        TestnetOrderInput? input = null;
        try
        {
            if (args.Length != 0) throw new InvalidOperationException();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
            using var buffer = new MemoryStream(); var chunk = new byte[4096];
            var stdin = Console.OpenStandardInput(); int length;
            while ((length = await stdin.ReadAsync(chunk, deadline.Token)) != 0)
            {
                if (buffer.Length + length > 16384) throw new InvalidOperationException();
                buffer.Write(chunk, 0, length);
            }
            input = JsonSerializer.Deserialize<TestnetOrderInput>(buffer.ToArray()) ?? throw new InvalidOperationException();
            input.Validate();
            var path = System.Environment.GetEnvironmentVariable("TRADEOPS_TESTNET_ATTEMPT_DIRECTORY");
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new InvalidOperationException();
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var result = await TestnetOrderQualification.RunAsync(input, http, path, deadline.Token);
            Console.WriteLine(JsonSerializer.Serialize(result));
            return result.Status == "TESTNET_ORDER_CAPTURE_PASS" ? 0 : 1;
        }
        catch
        {
            // Cleanup/transport failures cannot fabricate zero sends or a resolved exchange state.
            Console.WriteLine(JsonSerializer.Serialize(new { Status = "TESTNET_ORDER_FAILED_UNKNOWN",
                Environment = "Testnet", DatabaseUsed = (bool?)null, PlacementAttempts = (int?)null,
                CancellationAttempts = (int?)null, ExchangeOrderResolved = false, TradingReady = false }));
            return 1;
        }
        finally { if (input is not null) { input.ClientId = ""; input.ClientSecret = ""; } }
    }
}
