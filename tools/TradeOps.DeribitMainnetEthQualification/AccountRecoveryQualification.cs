using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.Domain.Oms;
using TradeOps.Infrastructure.Persistence.Oms;

namespace TradeOps.DeribitMainnetEthQualification;

internal static class AccountRecoveryQualification
{
    internal sealed class ReaderLease(IOmsPrivateRecoveryReader reader, IAsyncDisposable? resource = null) : IAsyncDisposable
    {
        internal IOmsPrivateRecoveryReader Reader { get; } = reader;
        public ValueTask DisposeAsync() => resource?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
    private sealed class Input
    {
        public string Purpose { get; set; } = "";
        public string Environment { get; set; } = "";
        public string ClientId { get; set; } = "";
        public string ClientSecret { get; set; } = "";
        public long ExpectedAccountId { get; set; }
        public string AccountType { get; set; } = "";
        public DateTimeOffset FromUtc { get; set; }
        public DateTimeOffset ThroughUtc { get; set; }
    }

    // Entire public diagnostics allowlist. Never serialize the assessment's scoped identity or private facts.
    internal sealed record Diagnostics(string Status, string Stage, bool MetadataValidated, bool DatabaseUsed, bool SelectOnly,
        bool AssessmentCompleted, int RequestedInstruments, bool ScopedAccountObservationsValidated, int AcceptedPositionRows,
        bool BalanceObserved, int RetainedFacts, int EnumeratedFacts, int KnownOrders, int AddressedOrders, int RetentionExpiredOrders,
        DateTimeOffset? FromUtc, DateTimeOffset? ThroughUtc, DateTimeOffset AssessedAtUtc,
        IReadOnlyList<string> AccountProblems, IReadOnlyList<string> Blockers, int RetainedTerminalOrdersValidated = 0)
    {
        public int SchemaVersion => 2;
        public string OrderInstrument => "ETH-PERPETUAL";
        public string ManifestPolicy => "EthActiveInverseFuturesV1";
        public string Currency => "ETH";
        public string Kind => "future";
        public bool AccountWideCoverage => false;
        public bool VenueCoherent => false;
        public bool AccountReady => false;
        public bool DurableProjectionPublished => false;
        public bool TradingReady => false;
        public int DurableWrites => 0;
        public int MutatingCalls => 0;
    }

    internal static async Task<int> RunAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(120));
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false,
            UseDefaultCredentials = false, AutomaticDecompression = DecompressionMethods.None };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var stdin = Console.OpenStandardInput();
        var result = await CollectAsync(stdin, http, async (_, token) => {
            var source = await RecoveryStore.OpenReader(token);
            return new ReaderLease(new OmsPostgresPrivateRecoveryReader(source), source);
        }, deadline.Token);
        Console.WriteLine(JsonSerializer.Serialize(result));
        return result.AssessmentCompleted ? 0 : 1; // Completed BLOCKED diagnostics never grant recovery or trading.
    }

    internal static async Task<Diagnostics> CollectAsync(Stream stdin, HttpClient http,
        Func<AccountKey, CancellationToken, Task<ReaderLease>> openReader, CancellationToken ct)
    {
        Input? input = null; var stage = "Input"; var metadata = false; var database = false; var selectOnly = false;
        var count = 0; DateTimeOffset? from = null, through = null;
        using var bytes = new MemoryStream();
        try
        {
            var chunk = new byte[4096]; int n;
            try
            {
                while ((n = await stdin.ReadAsync(chunk, ct)) != 0)
                {
                    if (bytes.Length+n > 16384) throw new InvalidOperationException();
                    bytes.Write(chunk, 0, n);
                }
            }
            finally { CryptographicOperations.ZeroMemory(chunk); }
            input = JsonSerializer.Deserialize<Input>(bytes.GetBuffer().AsSpan(0, (int)bytes.Length),
                new JsonSerializerOptions { MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
                ?? throw new InvalidOperationException();
            if (input.Purpose != "PINNED_ACCOUNT_RECOVERY_ASSESSMENT_V1" || input.Environment != "Mainnet" ||
                string.IsNullOrWhiteSpace(input.ClientId) || input.ClientId.Length > 512 || input.ClientId.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(input.ClientSecret) || input.ClientSecret.Length > 4096 || input.ClientSecret.Any(char.IsControl) ||
                input.FromUtc.Offset != TimeSpan.Zero || input.ThroughUtc.Offset != TimeSpan.Zero ||
                input.FromUtc <= DateTimeOffset.UnixEpoch.AddHours(1) || input.ThroughUtc < input.FromUtc ||
                input.ThroughUtc > DateTimeOffset.UtcNow || input.ThroughUtc-input.FromUtc > TimeSpan.FromDays(7))
                throw new InvalidOperationException();
            var scope = RecoveryStore.Scope(input.ExpectedAccountId, input.AccountType);
            from = input.FromUtc; through = input.ThroughUtc; stage = "Metadata";
            var catalog = await new DeribitInstrumentCatalog(http).ReadAsync(OmsEnvironment.Mainnet, "ETH", "future", ct);
            var instruments = DeribitEthFutureAccountManifest.Create(catalog, OmsEnvironment.Mainnet, DateTimeOffset.UtcNow);
            var plan = new DeribitPrivateCapturePlan(input.ExpectedAccountId, input.AccountType == "subaccount", instruments);
            DeribitInstrumentCatalog.ValidatePlan(OmsEnvironment.Mainnet, plan, new[] { catalog });
            count = instruments.Count; metadata = true; stage = "Store"; database = true;
            await using var lease = await openReader(scope, ct) ?? throw new InvalidOperationException(); selectOnly = true;
            stage = "Assessment";
            var binding = new DeribitPrivateCaptureBinding(scope, new() { WebSocketUrl = "wss://www.deribit.com/ws/api/v2",
                ClientId = input.ClientId, ClientSecret = input.ClientSecret }, plan);
            var query = new DeribitRecoveryQuery(scope, Guid.NewGuid(), "ETH-PERPETUAL", input.FromUtc, input.ThroughUtc,
                TimeSpan.FromMinutes(2), MaximumPagesPerSource: 5, MaximumFacts: 2000);
            var expected = instruments.Select(x => new DeribitAccountInstrumentExpectation(x.Name, "USD", "ETH")).ToArray();
            var gate = new DeribitRecoveryEvidenceGate(lease.Reader, new DeribitRecoveryQueries(binding, http), new DeribitKnownOrderReader(binding, http));
            var result = await gate.AssessAccountAsync(new(query, "ETH", OmsPrivateAmountUnit.Usd, "USD"),
                new(new(scope, query.Epoch, "ETH", "future", TimeSpan.FromSeconds(30)), expected), new DeribitAccountSnapshotReader(binding, http), ct);
            var e = result.Evidence;
            return new("RECOVERY_ACCOUNT_EVIDENCE_BLOCKED", "Completed", true, true, true, true, count,
                result.ScopedAccountObservationsValidated, result.AcceptedPositionRows, result.BalanceObserved,
                e.RetainedFacts, e.EnumeratedFacts, e.KnownOrders, e.AddressedOrders, e.RetentionExpiredOrders,
                from, through, e.AssessedAtUtc, result.AccountProblems.Select(x => x.ToString()).ToArray(), e.Blockers.Select(x => x.ToString()).ToArray(),
                e.RetainedTerminalOrdersValidated);
        }
        catch
        {
            return new("RECOVERY_ACCOUNT_ASSESSMENT_REFUSED", stage, metadata, database, selectOnly, false, count, false, 0, false,
                0, 0, 0, 0, 0, from, through, DateTimeOffset.UtcNow, Array.Empty<string>(), Array.Empty<string>());
        }
        finally
        {
            if (input is not null) { input.ClientId = ""; input.ClientSecret = ""; }
            CryptographicOperations.ZeroMemory(bytes.GetBuffer());
        }
    }
}
