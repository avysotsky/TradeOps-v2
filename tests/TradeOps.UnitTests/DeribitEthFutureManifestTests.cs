using System.Net;
using System.Text;
using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.Domain.Oms;
using TradeOps.DeribitMainnetEthQualification;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DeribitEthFutureManifestTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 15, 0, 0, TimeSpan.Zero);
    private static DeribitInstrumentMetadata Instrument(string name = "ETH-PERPETUAL", bool active = true) =>
        new(name, "future", "reversed", "ETH", "USD", "ETH", active, OmsPrivateAmountUnit.Usd, "USD");
    private static DeribitInstrumentCatalogSnapshot Catalog(params DeribitInstrumentMetadata[] items) =>
        new(OmsEnvironment.Mainnet, "ETH", "future", Now, items.Length == 0 ? new[] { Instrument() } : items);

    [Fact]
    public void ManifestIsSortedAndFrozenFromPublicMetadataBeforeAnyAccountRead()
    {
        var manifest = DeribitEthFutureAccountManifest.Create(Catalog(Instrument(), Instrument("ETH-27NOV26"),
            Instrument("INACTIVE", false)), OmsEnvironment.Mainnet, Now);
        Assert.Equal(new[] { "ETH-27NOV26", "ETH-PERPETUAL" }, manifest.Select(x => x.Name));
        Assert.All(manifest, x => { Assert.Equal("ETH", x.Currency); Assert.Equal("future", x.Kind);
            Assert.Equal(OmsPrivateAmountUnit.Usd, x.AmountUnit); Assert.Equal("USD", x.AmountCurrency); });
        Assert.Throws<NotSupportedException>(() => ((IList<DeribitPrivateInstrument>)manifest).Clear());
    }

    [Fact]
    public void ManifestAcceptsItsExactBoundWithoutDerivingUnitsFromNames()
    {
        var items = Enumerable.Range(1, 255).Select(i => Instrument("PUBLIC-" + i)).Prepend(Instrument()).ToArray();
        var result = DeribitEthFutureAccountManifest.Create(Catalog(items), OmsEnvironment.Mainnet, Now);
        Assert.Equal(256, result.Count); Assert.All(result, x => Assert.Equal("USD", x.AmountCurrency));
    }

    [Theory]
    [InlineData("environment")] [InlineData("currency")] [InlineData("kind")] [InlineData("empty")]
    [InlineData("anchor")] [InlineData("inactive")][InlineData("linear")] [InlineData("base")]
    [InlineData("quote")] [InlineData("settlement")] [InlineData("unit")] [InlineData("amount-currency")]
    [InlineData("stale")] [InlineData("future")] [InlineData("capacity")] [InlineData("duplicate")]
    [InlineData("offset")]
    public void WrongOrUnavailablePublicMetadataCannotProduceManifest(string mode)
    {
        var item = Instrument(); var received = Now; var now = Now; var env = OmsEnvironment.Mainnet; var currency = "ETH"; var kind = "future";
        switch (mode)
        {
            case "environment": env = OmsEnvironment.Testnet; break;
            case "currency": currency = "BTC"; break;
            case "kind": kind = "option"; break;
            case "anchor": item = item with { Name = "ETH-27NOV26" }; break;
            case "inactive": item = item with { IsActive = false }; break;
            case "linear": item = item with { InstrumentType = "linear" }; break;
            case "base": item = item with { BaseCurrency = "BTC" }; break;
            case "quote": item = item with { QuoteCurrency = "USDC" }; break;
            case "settlement": item = item with { SettlementCurrency = "USDC" }; break;
            case "unit": item = item with { AmountUnit = OmsPrivateAmountUnit.BaseCoin }; break;
            case "amount-currency": item = item with { AmountCurrency = "ETH" }; break;
            case "stale": received = Now.AddSeconds(-31); break;
            case "future": received = Now.AddSeconds(1); break;
            case "offset": now = Now.ToOffset(TimeSpan.FromHours(2)); break;
        }
        var items = mode switch {
            "empty" => Array.Empty<DeribitInstrumentMetadata>(), "duplicate" => new[] { item, item },
            "capacity" => Enumerable.Range(1, 256).Select(i => Instrument("PUBLIC-" + i)).Prepend(item).ToArray(),
            _ => new[] { item } };
        var snapshot = new DeribitInstrumentCatalogSnapshot(env, currency, kind, received, items);
        Assert.Throws<InvalidOperationException>(() => DeribitEthFutureAccountManifest.Create(snapshot, OmsEnvironment.Mainnet, now));
    }

    [Fact]
    public async Task ExplicitGroupModeAcceptsDatedFutureAndRetainsScopeWithoutPrivateOutput()
    {
        using var venue = new Venue(); using var http = new HttpClient(venue); using var stdin = Stdin();
        var result = await AccountSnapshotQualification.CollectAsync(stdin, http, CancellationToken.None, ethInverseFutures: true);
        Assert.Equal("ACCOUNT_SNAPSHOT_SCOPED_PASS", result.Status); Assert.Equal("EthActiveInverseFuturesV1", result.ManifestPolicy);
        Assert.Equal(2, result.SchemaVersion); Assert.Equal(2, result.RequestedInstrumentCount); Assert.Equal(1, result.PositionCount);
        Assert.True(result.NativeBindingVerified); Assert.True(result.BalanceObserved); Assert.True(result.FreshAtAssessment);
        Assert.Equal(5, venue.Calls); AssertSafe(result);
    }

    [Theory]
    [InlineData("unmapped")] [InlineData("inactive-position")] [InlineData("expired-position")]
    public async Task UncoveredRowsNeverExpandManifestOrTriggerMetadataFallback(string mode)
    {
        using var venue = new Venue { Mode = mode }; using var http = new HttpClient(venue); using var stdin = Stdin();
        var result = await AccountSnapshotQualification.CollectAsync(stdin, http, CancellationToken.None, ethInverseFutures: true);
        Assert.Equal("ACCOUNT_SNAPSHOT_INCOMPLETE", result.Status); Assert.Contains("UnmappedInstrument", result.Problems);
        Assert.Equal(0, result.PositionCount); Assert.False(result.BalanceObserved); Assert.Equal(5, venue.Calls);
        Assert.Equal(2, result.RequestedInstrumentCount); AssertSafe(result);
    }

    [Theory]
    [InlineData("linear")] [InlineData("base")] [InlineData("duplicate")] [InlineData("anchor")]
    public async Task UnsupportedGroupCatalogRefusesBeforeAuth(string mode)
    {
        using var venue = new Venue { Mode = mode }; using var http = new HttpClient(venue); using var stdin = Stdin();
        var result = await AccountSnapshotQualification.CollectAsync(stdin, http, CancellationToken.None, ethInverseFutures: true);
        Assert.Equal("ACCOUNT_SNAPSHOT_REFUSED", result.Status); Assert.Equal("Metadata", result.Stage);
        Assert.False(result.MetadataValidated); Assert.Equal(0, result.RequestedInstrumentCount); Assert.Equal(1, venue.Calls); AssertSafe(result);
    }

    [Theory]
    [InlineData(true, "READ_ONLY_ETH_ACCOUNT_SNAPSHOT_V1")]
    [InlineData(false, "READ_ONLY_ETH_INVERSE_FUTURES_V1")]
    public async Task EntryModeAndPurposeMustAgreeBeforeAnyNetwork(bool group, string purpose)
    {
        using var venue = new Venue(); using var http = new HttpClient(venue); using var stdin = Stdin(purpose);
        var result = await AccountSnapshotQualification.CollectAsync(stdin, http, CancellationToken.None, ethInverseFutures: group);
        Assert.Equal("ACCOUNT_SNAPSHOT_REFUSED", result.Status); Assert.Equal("Input", result.Stage);
        Assert.Equal(0, venue.Calls); AssertSafe(result);
    }

    [Fact]
    public async Task GroupModeStillRefusesChangedNativeIdentity()
    {
        using var venue = new Venue { Mode = "account" }; using var http = new HttpClient(venue); using var stdin = Stdin();
        var result = await AccountSnapshotQualification.CollectAsync(stdin, http, CancellationToken.None, ethInverseFutures: true);
        Assert.Equal("ACCOUNT_SNAPSHOT_INCOMPLETE", result.Status); Assert.Contains("AccountMismatch", result.Problems);
        Assert.Equal(0, result.PositionCount); Assert.False(result.BalanceObserved); Assert.Equal(5, venue.Calls); AssertSafe(result);
    }

    private static MemoryStream Stdin(string purpose = "READ_ONLY_ETH_INVERSE_FUTURES_V1") => new(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
        Purpose = purpose, Environment = "Mainnet", ClientId = "fixture-id", ClientSecret = "fixture-secret", ExpectedAccountId = 123, AccountType = "main" })));
    private static void AssertSafe(AccountSnapshotQualification.Diagnostics result)
    {
        Assert.False(result.AccountWideCoverage); Assert.False(result.VenueCoherent); Assert.False(result.AccountReady);
        Assert.False(result.TradingReady); Assert.False(result.DurableProjectionPublished); Assert.False(result.DatabaseUsed);
        Assert.Equal(0, result.DurableWrites); Assert.Equal(0, result.MutatingCalls);
        var json = JsonSerializer.Serialize(result);
        foreach (var forbidden in new[] { "fixture-secret", "ETH-27NOV26", "UNMAPPED", "6543.210987", "9876.54321", "NativeSize", "Equity", "CashBalance", "observed-native:" })
            Assert.DoesNotContain(forbidden, json);
    }

    private sealed class Venue : HttpMessageHandler
    {
        internal int Calls; internal string Mode = "valid"; private int summaries;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Assert.Equal("www.deribit.com", request.RequestUri!.Host);
            var method = request.RequestUri.AbsolutePath.Replace("/api/v2/", ""); object result; long id = 1;
            if (method == "public/get_instruments")
            {
                Assert.Equal(HttpMethod.Get, request.Method); Assert.Null(request.Headers.Authorization);
                Assert.Equal("?currency=ETH&kind=future&expired=false", request.RequestUri.Query);
                var items = new List<object> { Metadata("ETH-PERPETUAL"), Metadata("ETH-27NOV26") };
                if (Mode == "anchor") items.RemoveAt(0);
                if (Mode == "duplicate") items.Add(Metadata("ETH-PERPETUAL"));
                if (Mode == "inactive-position") items.Add(Metadata("INACTIVE", false));
                result = items;
            }
            else
            {
                Assert.Equal(HttpMethod.Post, request.Method); Assert.Empty(request.RequestUri.Query);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                id = body.RootElement.GetProperty("id").GetInt64(); var p = body.RootElement.GetProperty("params");
                if (method == "public/auth")
                {
                    Assert.Null(request.Headers.Authorization); var scope = p.GetProperty("scope").GetString()!;
                    Assert.DoesNotContain("read_write", scope); result = new { access_token = "fixture-token", scope, expires_in = 300 };
                }
                else
                {
                    Assert.Equal("fixture-token", request.Headers.Authorization?.Parameter);
                    Assert.Equal("ETH", p.GetProperty("currency").GetString()); Assert.Equal(2, p.EnumerateObject().Count());
                    if (method == "private/get_account_summary")
                    {
                        Assert.True(p.GetProperty("extended").GetBoolean()); summaries++;
                        result = new { id = Mode == "account" && summaries == 2 ? 124 : 123, type = "main", currency = "ETH",
                            balance = 6543.210987m, equity = 9876.54321m, cross_collateral_enabled = false, portfolio_margining_enabled = false };
                    }
                    else
                    {
                        Assert.Equal("private/get_positions", method); Assert.Equal("future", p.GetProperty("kind").GetString());
                        var name = Mode switch { "unmapped" => "UNMAPPED", "inactive-position" => "INACTIVE", "expired-position" => "EXPIRED", _ => "ETH-27NOV26" };
                        result = new[] { new { instrument_name = name, kind = "future", user_id = 123, direction = "sell", size = -10, size_currency = -0.005m } };
                    }
                }
            }
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result })) };
        }
        private object Metadata(string name, bool active = true) => new { instrument_name = name, kind = "future",
            instrument_type = Mode == "linear" ? "linear" : "reversed", base_currency = Mode == "base" ? "BTC" : "ETH",
            quote_currency = "USD", settlement_currency = Mode == "base" ? "BTC" : "ETH", is_active = active };
    }
}
