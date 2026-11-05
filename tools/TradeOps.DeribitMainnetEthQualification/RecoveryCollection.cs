using System.Net;
using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.Domain.Oms;

namespace TradeOps.DeribitMainnetEthQualification;

internal static class RecoveryCollection
{
    private sealed class Input
    {
        public string ClientId { get; set; } = "";
        public string ClientSecret { get; set; } = "";
        public long ExpectedAccountId { get; set; }
        public string AccountType { get; set; } = "";
        public DateTimeOffset FromUtc { get; set; }
        public DateTimeOffset ThroughUtc { get; set; }
    }

    internal static async Task<int> RunAsync(CancellationToken ct)
    {
        Input? input = null;
        try
        {
            using var bytes = new MemoryStream(); var chunk = new byte[4096]; int n;
            var stdin = Console.OpenStandardInput();
            while ((n = await stdin.ReadAsync(chunk, ct)) != 0)
            {
                if (bytes.Length+n > 16384) throw new InvalidOperationException();
                bytes.Write(chunk, 0, n);
            }
            input = JsonSerializer.Deserialize<Input>(bytes.ToArray()) ?? throw new InvalidOperationException();
            if (string.IsNullOrWhiteSpace(input.ClientId) || input.ClientId.Length > 512 || input.ClientId.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(input.ClientSecret) || input.ClientSecret.Length > 4096 || input.ClientSecret.Any(char.IsControl))
                throw new InvalidOperationException();
            var scope = RecoveryStore.Scope(input.ExpectedAccountId, input.AccountType);
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false,
                AutomaticDecompression = DecompressionMethods.None };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var instrument = new DeribitPrivateInstrument("ETH-PERPETUAL", "future", "ETH", OmsPrivateAmountUnit.Usd, "USD");
            var plan = new DeribitPrivateCapturePlan(input.ExpectedAccountId, input.AccountType == "subaccount", new[] { instrument });
            var catalog = await new DeribitInstrumentCatalog(http).ReadAsync(OmsEnvironment.Mainnet, "ETH", "future", ct);
            DeribitInstrumentCatalog.ValidatePlan(OmsEnvironment.Mainnet, plan, new[] { catalog });
            var options = new DeribitOptions { WebSocketUrl = "wss://www.deribit.com/ws/api/v2", ClientId = input.ClientId, ClientSecret = input.ClientSecret };
            var result = await new DeribitRecoveryQueries(new(scope, options, plan), http).CollectAsync(
                new(scope, Guid.NewGuid(), instrument.Name, input.FromUtc, input.ThroughUtc, TimeSpan.FromMinutes(2)), ct);
            Console.WriteLine(JsonSerializer.Serialize(new {
                Status = result.TraversalComplete ? "RECOVERY_QUERY_TRAVERSAL_PASS" : "RECOVERY_QUERY_INCOMPLETE",
                Instrument = instrument.Name, result.QueryFromUtc, result.ThroughUtc, result.StartedAtUtc, result.CompletedAtUtc,
                result.TraversalComplete, result.CoherentRecovery, result.Sources,
                FactCount = result.Observations.Count,
                OrderReports = result.Observations.Count(x => x.Facts.Kind == OmsJournalEventKind.Order),
                Executions = result.Observations.Count(x => x.Facts.Kind == OmsJournalEventKind.Execution),
                Problems = result.Problems.Select(x => x.ToString()), DatabaseUsed = false, DurableWrites = 0,
                MutatingCalls = 0, AccountWideCoverage = false, HistoricalIndexingVerified = false, TradingReady = false }));
            return result.TraversalComplete ? 0 : 1;
        }
        catch
        {
            Console.WriteLine("{\"Status\":\"RECOVERY_QUERY_REFUSED\",\"DatabaseUsed\":false,\"MutatingCalls\":0,\"TradingReady\":false}");
            return 1;
        }
        finally { if (input is not null) { input.ClientId = ""; input.ClientSecret = ""; } }
    }
}
