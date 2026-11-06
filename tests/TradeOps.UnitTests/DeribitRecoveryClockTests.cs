using System.Net;
using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.Domain.Oms;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DeribitRecoveryClockTests
{
    private static readonly AccountKey Scope = new(new("tenant"), new("DERIBIT"), OmsEnvironment.Testnet, new("synthetic-account"));
    [Fact]
    public async Task NativeTimeIsMeasuredWithoutAuthenticationOrLocalClockMutation()
    {
        using var venue = new Venue(); using var http = new HttpClient(venue); var result = await Reader(http).ReadAsync(new(Scope, Guid.NewGuid()));
        Assert.Equal(1, venue.Calls); Assert.Equal(venue.NativeTime, result.VenueTimeUtc);
        Assert.True(result.ReceivedAtUtc >= result.RequestedAtUtc); Assert.Equal(Scope, result.Scope);
    }
    [Fact]
    public async Task LostResponseReturnsNoInferredVenueTimestampAndNeverRetries()
    {
        using var venue = new Venue { Fail = true }; using var http = new HttpClient(venue);
        var result = await Reader(http).ReadAsync(new(Scope, Guid.NewGuid())); Assert.Null(result.VenueTimeUtc); Assert.Equal(1, venue.Calls);
    }
    [Fact]
    public void NativeOrderFingerprintIgnoresJsonFormattingButDetectsTermsChangesAndDuplicateKeys()
    {
        using var a = JsonDocument.Parse("""{"amount":1.00,"order_id":"id","price":2000}""");
        using var b = JsonDocument.Parse("""{"price":2000.0,"order_id":"id","amount":1}""");
        using var changed = JsonDocument.Parse("""{"amount":2,"order_id":"id","price":2000}""");
        using var collision = JsonDocument.Parse("""{"amount":1,"amount":2}""");
        Assert.Equal(DeribitNativeOrderFingerprint.Create(a.RootElement), DeribitNativeOrderFingerprint.Create(b.RootElement));
        Assert.NotEqual(DeribitNativeOrderFingerprint.Create(a.RootElement), DeribitNativeOrderFingerprint.Create(changed.RootElement));
        Assert.Throws<FormatException>(() => DeribitNativeOrderFingerprint.Create(collision.RootElement));
    }
    private static DeribitRecoveryClockReader Reader(HttpClient http) => new(new(Scope,
        new() { ClientId = "fixture-id", ClientSecret = "fixture-secret" },
        new(123, false, [new("ETH-PERPETUAL", "future", "ETH", OmsPrivateAmountUnit.Usd, "USD")])), http);
    private sealed class Venue : HttpMessageHandler
    {
        internal int Calls; internal bool Fail; internal DateTimeOffset NativeTime;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Assert.Equal("test.deribit.com", request.RequestUri!.Host); Assert.Equal("/api/v2/public/get_time", request.RequestUri.AbsolutePath);
            Assert.Null(request.Headers.Authorization); Assert.Empty(request.RequestUri.Query);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); var root = body.RootElement;
            Assert.Equal("public/get_time", root.GetProperty("method").GetString()); Assert.Empty(root.GetProperty("params").EnumerateObject());
            if (Fail) throw new IOException("synthetic lost response");
            NativeTime = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(JsonSerializer.Serialize(new {
                jsonrpc = "2.0", id = root.GetProperty("id").GetInt64(), result = NativeTime.ToUnixTimeMilliseconds() })) };
        }
    }
}
