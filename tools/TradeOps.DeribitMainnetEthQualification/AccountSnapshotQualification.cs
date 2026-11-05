using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.Domain.Oms;

namespace TradeOps.DeribitMainnetEthQualification;

// Dedicated opt-in read mode. This path never opens a database or enters the order runner.
internal static class AccountSnapshotQualification
{
    internal sealed class Input
    {
        public string Purpose { get; set; } = "";
        public string Environment { get; set; } = "";
        public string ClientId { get; set; } = "";
        public string ClientSecret { get; set; } = "";
        public long ExpectedAccountId { get; set; }
        public string AccountType { get; set; } = "";
    }

    // This is the entire output allowlist. Never serialize an account result or an exception.
    internal sealed record Diagnostics(string Status, string Stage, bool MetadataValidated, bool QueriesCompleted,
        bool NativeBindingVerified, bool FreshAtAssessment, int PositionCount, bool BalanceObserved,
        DateTimeOffset AssessedAtUtc, string[] Problems)
    {
        public string Currency => "ETH";
        public string Kind => "future";
        public int RequestedInstrumentCount => 1;
        public bool AccountWideCoverage => false;
        public bool VenueCoherent => false;
        public bool AccountReady => false;
        public bool DurableProjectionPublished => false;
        public bool TradingReady => false;
        public bool DatabaseUsed => false;
        public int DurableWrites => 0;
        public int MutatingCalls => 0;
    }

    internal static async Task<int> RunAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(75));
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false,
            UseDefaultCredentials = false, AutomaticDecompression = DecompressionMethods.None };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var stdin = Console.OpenStandardInput();
        var result = await CollectAsync(stdin, http, deadline.Token);
        Console.WriteLine(JsonSerializer.Serialize(result));
        return result.Status == "ACCOUNT_SNAPSHOT_SCOPED_PASS" ? 0 : 1;
    }

    internal static async Task<Diagnostics> CollectAsync(Stream stdin, HttpClient http, CancellationToken ct,
        TimeProvider? timeProvider = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        var stage = "Input"; var metadataValidated = false; Input? input = null;
        using var bytes = new MemoryStream();
        try
        {
            var chunk = new byte[4096]; int n;
            try
            {
                while ((n = await stdin.ReadAsync(chunk, ct)) != 0)
                {
                    if (bytes.Length + n > 16384) throw new InvalidOperationException();
                    bytes.Write(chunk, 0, n);
                }
            }
            finally { CryptographicOperations.ZeroMemory(chunk); }
            input = JsonSerializer.Deserialize<Input>(bytes.GetBuffer().AsSpan(0, (int)bytes.Length),
                new JsonSerializerOptions { MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
                ?? throw new InvalidOperationException();
            if (input.Purpose != "READ_ONLY_ETH_ACCOUNT_SNAPSHOT_V1" || input.Environment != "Mainnet" ||
                string.IsNullOrWhiteSpace(input.ClientId) || input.ClientId.Length > 512 || input.ClientId.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(input.ClientSecret) || input.ClientSecret.Length > 4096 || input.ClientSecret.Any(char.IsControl))
                throw new InvalidOperationException();
            var scope = RecoveryStore.Scope(input.ExpectedAccountId, input.AccountType);
            var instrument = new DeribitPrivateInstrument("ETH-PERPETUAL", "future", "ETH", OmsPrivateAmountUnit.Usd, "USD");
            var plan = new DeribitPrivateCapturePlan(input.ExpectedAccountId, input.AccountType == "subaccount", new[] { instrument });
            stage = "Metadata";
            var catalog = await new DeribitInstrumentCatalog(http).ReadAsync(OmsEnvironment.Mainnet, "ETH", "future", ct);
            DeribitInstrumentCatalog.ValidatePlan(OmsEnvironment.Mainnet, plan, new[] { catalog });
            var actual = catalog.Instruments.Single(x => x.Name == instrument.Name);
            if (!actual.IsActive || actual.InstrumentType != "reversed" || actual.BaseCurrency != "ETH" ||
                actual.QuoteCurrency != "USD" || actual.SettlementCurrency != "ETH") throw new InvalidOperationException();
            metadataValidated = true; stage = "AccountQueries";
            var options = new DeribitOptions { WebSocketUrl = "wss://www.deribit.com/ws/api/v2",
                ClientId = input.ClientId, ClientSecret = input.ClientSecret };
            var result = await new DeribitAccountSnapshotReader(new(scope, options, plan), http, clock).ReadAsync(
                new(scope, Guid.NewGuid(), "ETH", "future", TimeSpan.FromSeconds(30), 256), ct);
            var assessed = clock.GetUtcNow(); var fresh = result.IsFreshAt(assessed);
            var problems = result.Problems.Select(x => x.ToString()).ToList();
            if (result.QueriesCompleted && !fresh) problems.Add(nameof(DeribitAccountSnapshotProblem.StaleObservation));
            return new(fresh ? "ACCOUNT_SNAPSHOT_SCOPED_PASS" : "ACCOUNT_SNAPSHOT_INCOMPLETE", "Completed", true,
                result.QueriesCompleted, result.QueriesCompleted, fresh, fresh ? result.Positions.Count : 0,
                fresh && result.Balance is not null, assessed, problems.Distinct().ToArray());
        }
        catch
        {
            return new("ACCOUNT_SNAPSHOT_REFUSED", stage, metadataValidated, false, false, false, 0, false,
                clock.GetUtcNow(), Array.Empty<string>());
        }
        finally
        {
            if (input is not null) { input.ClientId = ""; input.ClientSecret = ""; }
            CryptographicOperations.ZeroMemory(bytes.GetBuffer());
        }
    }
}
