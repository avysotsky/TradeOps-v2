using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace TradeOps.DeribitTestnetOrderQualification;

// This acceptance transport has no configurable destination, retries or production registration.
internal sealed class TestnetRpc(HttpClient http)
{
    private long id;
    internal string? Token { private get; set; }
    internal async Task<JsonElement> Call(string method, object parameters, CancellationToken ct)
    {
        if (method is not ("public/auth" or "public/get_instrument" or "public/get_order_book" or
            "private/get_account_summary" or "private/get_open_orders_by_currency" or "private/get_positions" or
            "private/buy" or "private/cancel_by_label" or "private/get_order_state_by_label"))
            throw new InvalidOperationException("Unsupported acceptance method.");
        var requestId = Interlocked.Increment(ref id);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://test.deribit.com/api/v2/" + method)
        { Content = JsonContent.Create(new { jsonrpc = "2.0", id = requestId, method, @params = parameters }) };
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        if (method.StartsWith("private/", StringComparison.Ordinal))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token ?? throw new InvalidOperationException());
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri != request.RequestUri)
            throw new InvalidOperationException("Testnet request failed.");
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int length;
        while ((length = await body.ReadAsync(chunk, ct)) != 0)
        {
            if (buffer.Length + length > 262144) throw new InvalidOperationException("Bounded reply exceeded.");
            buffer.Write(chunk, 0, length);
        }
        using var json = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        var root = json.RootElement;
        if (root.GetProperty("jsonrpc").GetString() != "2.0" || root.GetProperty("id").GetInt64() != requestId ||
            root.TryGetProperty("error", out _)) throw new InvalidOperationException("Testnet RPC failed.");
        return root.GetProperty("result").Clone();
    }
}
