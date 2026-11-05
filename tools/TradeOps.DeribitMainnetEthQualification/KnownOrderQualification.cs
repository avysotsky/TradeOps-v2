using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.Domain.Oms;
using TradeOps.Infrastructure.Persistence.Oms;

namespace TradeOps.DeribitMainnetEthQualification;

internal static class KnownOrderQualification
{
    private sealed class Input
    {
        public string Purpose { get; set; } = "";
        public string ClientId { get; set; } = "";
        public string ClientSecret { get; set; } = "";
        public long ExpectedAccountId { get; set; }
        public string AccountType { get; set; } = "";
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
            if (input.Purpose != "PINNED_KNOWN_ORDER_READ" || string.IsNullOrWhiteSpace(input.ClientId) || input.ClientId.Length > 512 || input.ClientId.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(input.ClientSecret) || input.ClientSecret.Length > 4096 || input.ClientSecret.Any(char.IsControl))
                throw new InvalidOperationException();
            var scope = RecoveryStore.Scope(input.ExpectedAccountId, input.AccountType);
            await using var source = await RecoveryStore.OpenReader(ct);
            var snapshot = await new OmsPostgresPrivateRecoveryReader(source).ReadAsync(scope, cancellationToken: ct);
            var request = DeribitKnownOrderSelection.FromSnapshot(snapshot, scope, Guid.NewGuid(), "ETH-PERPETUAL");
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var plan = new DeribitPrivateCapturePlan(input.ExpectedAccountId, input.AccountType == "subaccount", new[] {
                new DeribitPrivateInstrument("ETH-PERPETUAL", "future", "ETH", OmsPrivateAmountUnit.Usd, "USD") });
            var catalog = await new DeribitInstrumentCatalog(http).ReadAsync(OmsEnvironment.Mainnet, "ETH", "future", ct);
            DeribitInstrumentCatalog.ValidatePlan(OmsEnvironment.Mainnet, plan, new[] { catalog });
            var options = new DeribitOptions { WebSocketUrl = "wss://www.deribit.com/ws/api/v2", ClientId = input.ClientId, ClientSecret = input.ClientSecret };
            var result = await new DeribitKnownOrderReader(new(scope, options, plan), http).ReadAsync(request, ct);
            Console.WriteLine(JsonSerializer.Serialize(new {
                Status = result.QueriesCompleted ? result.AllOrdersFound ? "KNOWN_ORDERS_FOUND" : "KNOWN_ORDERS_UNRESOLVED" : "KNOWN_ORDER_READ_FAILED",
                result.RequestedOrders, result.QueriesCompleted, result.AllOrdersFound, result.StartedAtUtc, result.CompletedAtUtc,
                Found = result.Lookups.Count(x => x.Outcome == DeribitKnownOrderLookupOutcome.Found),
                Missing = result.Lookups.Count(x => x.Outcome == DeribitKnownOrderLookupOutcome.Missing),
                Unsupported = result.Lookups.Count(x => x.Outcome == DeribitKnownOrderLookupOutcome.Unsupported),
                VenueCodes = result.Lookups.Where(x => x.VenueCode.HasValue).Select(x => x.VenueCode).Distinct(),
                CurrentFacts = result.Observations.Count, RetainedFacts = snapshot.AcceptedFactCount,
                Problems = result.Problems.Select(x => x.ToString()), result.HttpStatusCode,
                DatabaseUsed = true, SelectOnly = true, DurableWrites = 0, MutatingCalls = 0,
                CoherentRecovery = false, OrderHistoryComplete = false, TradingReady = false }));
            return result.QueriesCompleted ? 0 : 1; // An explicit unresolved lookup is a completed query, never recovery success.
        }
        catch
        {
            Console.WriteLine("{\"Status\":\"KNOWN_ORDER_READ_REFUSED\",\"DurableWrites\":0,\"MutatingCalls\":0,\"TradingReady\":false}");
            return 1;
        }
        finally { if (input is not null) { input.ClientId = ""; input.ClientSecret = ""; } }
    }
}
