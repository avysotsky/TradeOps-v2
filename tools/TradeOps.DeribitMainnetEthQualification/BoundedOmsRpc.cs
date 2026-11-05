using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace TradeOps.DeribitMainnetEthQualification;

// Dedicated owner-launched qualification transport. No redirects/retries/dynamic origin.
internal sealed class BoundedOmsRpc(HttpClient http)
{
    internal sealed record Reply(Guid SessionId, long Id, string Method, DateTimeOffset SentAtUtc,
        DateTimeOffset ReceivedAtUtc, JsonElement Root)
    { internal JsonElement Result => Root.GetProperty("result"); }
    private readonly Guid sessionId = Guid.NewGuid();
    private long nextId;
    internal string? Token { private get; set; }

    internal async Task<Reply> CallAsync(string method, object parameters, CancellationToken ct)
    {
        if (method is not ("public/auth" or "public/get_instrument" or "public/get_order_book" or "public/get_time" or
            "private/get_account_summary" or "private/get_positions" or "private/get_open_orders_by_currency" or
            "private/get_order_state" or "private/get_user_trades_by_order" or "private/buy" or "private/cancel") ||
            http.DefaultRequestHeaders.Authorization is not null || http.DefaultRequestHeaders.Contains("Cookie"))
            throw new InvalidOperationException("Unsupported bounded RPC method/client.");
        var id = Interlocked.Increment(ref nextId);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://www.deribit.com/api/v2/" + method)
        { Content = JsonContent.Create(new { jsonrpc = "2.0", id, method, @params = parameters }) };
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        if (method.StartsWith("private/", StringComparison.Ordinal))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token ?? throw new InvalidOperationException());
        var sent = DateTimeOffset.UtcNow;
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri != request.RequestUri)
            throw new InvalidOperationException("Bounded RPC refused.");
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream(); var chunk = new byte[4096]; int n;
        while ((n = await body.ReadAsync(chunk, ct)) != 0)
        {
            if (buffer.Length + n > 262144) throw new InvalidOperationException("Bounded reply exceeded.");
            buffer.Write(chunk, 0, n);
        }
        using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        Unique(root);
        if (root.GetProperty("jsonrpc").GetString() != "2.0" || root.GetProperty("id").GetInt64() != id ||
            root.TryGetProperty("error", out _)) throw new InvalidOperationException("Uncorrelated/failed bounded reply.");
        return new(sessionId, id, method, sent, DateTimeOffset.UtcNow, root.Clone());
    }

    private static void Unique(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in item.EnumerateObject())
            { if (!names.Add(field.Name)) throw new InvalidOperationException("Duplicate bounded response field."); Unique(field.Value); }
        }
        else if (item.ValueKind == JsonValueKind.Array) foreach (var child in item.EnumerateArray()) Unique(child);
    }
}
