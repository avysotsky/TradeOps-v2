using System.Net;
using System.Text;
using System.Text.Json;
using TradeOps.DeribitMainnetEthQualification;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DeribitBoundedOmsRpcTests
{
    [Theory]
    [InlineData("private/cancel_all")] [InlineData("private/cancel_by_label")] [InlineData("private/close_position")]
    public async Task UnapprovedMutationHasNoHttpRequest(string method)
    {
        var handler = new Handler("normal"); using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new BoundedOmsRpc(http).CallAsync(method, new { }, CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("id")] [InlineData("duplicate")] [InlineData("error")]
    public async Task InvalidReplyCannotBecomeCorrelatedResponse(string mode)
    {
        var handler = new Handler(mode); using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new BoundedOmsRpc(http).CallAsync("public/get_time", new { }, CancellationToken.None));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task SuccessfulReplyRetainsOriginalSessionIdRequestIdAndTimes()
    {
        var handler = new Handler("normal"); using var http = new HttpClient(handler); var rpc = new BoundedOmsRpc(http);
        var first = await rpc.CallAsync("public/get_time", new { }, CancellationToken.None);
        var second = await rpc.CallAsync("public/get_time", new { }, CancellationToken.None);
        Assert.NotEqual(Guid.Empty, first.SessionId); Assert.Equal(first.SessionId, second.SessionId);
        Assert.True(first.Id < second.Id); Assert.True(first.ReceivedAtUtc >= first.SentAtUtc);
    }

    private sealed class Handler(string mode) : HttpMessageHandler
    {
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); var id = doc.RootElement.GetProperty("id").GetInt64();
            var text = mode switch { "id" => $"{{\"jsonrpc\":\"2.0\",\"id\":{id+1},\"result\":1}}",
                "duplicate" => $"{{\"jsonrpc\":\"2.0\",\"id\":{id+1},\"id\":{id},\"result\":1}}",
                "error" => $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":1,\"error\":{{\"code\":1}}}}",
                _ => $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":1}}" };
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }
}
