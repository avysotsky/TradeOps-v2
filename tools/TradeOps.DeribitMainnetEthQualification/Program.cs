using System.Text.Json;
using TradeOps.DeribitTestnetOrderQualification;

namespace TradeOps.DeribitMainnetEthQualification;

internal static class MainnetEthEntryPoint
{
    public static async Task<int> Main(string[] args)
    {
        TestnetOrderInput? input = null;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
            if (args.SequenceEqual(new[] { "--store-check" }))
            {
                await using var database = await QualificationDatabase.OpenAsync(true, deadline.Token);
                Console.WriteLine("{\"Status\":\"MAINNET_STORE_READY\",\"PersistentStore\":true,\"PlacementAttempts\":0,\"CancellationAttempts\":0}");
                return 0;
            }
            if (args.Length != 0) throw new InvalidOperationException();
            using var buffer = new MemoryStream(); var chunk = new byte[4096];
            var stdin = Console.OpenStandardInput(); int length;
            while ((length = await stdin.ReadAsync(chunk, deadline.Token)) != 0)
            {
                if (buffer.Length + length > 16384) throw new InvalidOperationException();
                buffer.Write(chunk, 0, length);
            }
            input = JsonSerializer.Deserialize<TestnetOrderInput>(buffer.ToArray()) ?? throw new InvalidOperationException();
            input.ValidateMainnetEth();
            var directory = Environment.GetEnvironmentVariable("TRADEOPS_MAINNET_ATTEMPT_DIRECTORY");
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) throw new InvalidOperationException();
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var result = await TestnetOrderQualification.RunMainnetEthAsync(input, http, directory, deadline.Token);
            Console.WriteLine(JsonSerializer.Serialize(result));
            return result.Status == "MAINNET_ETH_CAPTURE_PASS" ? 0 : 1;
        }
        catch
        {
            Console.WriteLine("{\"Status\":\"MAINNET_ETH_FAILED_UNKNOWN\",\"PlacementAttempts\":null,\"CancellationAttempts\":null,\"ExchangeOrderResolved\":false,\"TradingReady\":false,\"JournalRetained\":null}");
            return 1;
        }
        finally { if (input is not null) { input.ClientId = ""; input.ClientSecret = ""; } }
    }
}
