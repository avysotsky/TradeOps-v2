using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;

namespace TradeOps.Connectors.Deribit;

internal sealed class DeribitRecoveryQueryException(DeribitRecoveryQueryProblem problem, int? httpStatusCode = null, int? venueCode = null) : Exception("Recovery query refused.")
{
    internal DeribitRecoveryQueryProblem Problem { get; } = problem;
    internal int? HttpStatusCode { get; } = httpStatusCode;
    internal int? VenueCode { get; } = venueCode;
}

// Fixed-origin, read-only allowlist; no retries, credentials in URLs or redirect support.
internal sealed class DeribitRecoveryRpc(HttpClient http, OmsEnvironment environment)
{
    private long id;
    private int bytes;
    internal string? Token { private get; set; }
    internal async Task<JsonElement> Call(string method, object parameters, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (method is not ("public/auth" or "private/get_account_summary" or "private/get_open_orders_by_instrument" or
            "private/get_order_history_by_instrument" or "private/get_user_trades_by_instrument" or "private/get_order_state" or "private/get_positions"))
            throw new DeribitRecoveryQueryException(DeribitRecoveryQueryProblem.Protocol);
        if (id != 0) await Task.Delay(TimeSpan.FromMilliseconds(150), ct);
        var requestId = ++id;
        var host = environment == OmsEnvironment.Mainnet ? "www.deribit.com" : "test.deribit.com";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/api/v2/{method}")
        { Content = JsonContent.Create(new { jsonrpc = "2.0", id = requestId, method, @params = parameters }) };
        request.Headers.CacheControl = new() { NoStore = true };
        if (method.StartsWith("private/", StringComparison.Ordinal))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token ?? throw new InvalidOperationException());
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.RequestMessage?.RequestUri != request.RequestUri ||
            (!response.IsSuccessStatusCode && (method != "private/get_order_state" || (int)response.StatusCode != 400)))
            throw new DeribitRecoveryQueryException(DeribitRecoveryQueryProblem.Transport, (int)response.StatusCode);
        if (response.Content.Headers.ContentLength > 524288)
            throw new DeribitRecoveryQueryException(DeribitRecoveryQueryProblem.Capacity);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream(); var chunk = new byte[8192]; int n;
        while ((n = await stream.ReadAsync(chunk, ct)) != 0)
        {
            bytes += n;
            if (buffer.Length + n > 524288 || bytes > 8 * 1024 * 1024)
                throw new DeribitRecoveryQueryException(DeribitRecoveryQueryProblem.Capacity);
            buffer.Write(chunk, 0, n);
        }
        using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.GetProperty("jsonrpc").GetString() != "2.0" || root.GetProperty("id").GetInt64() != requestId)
            throw new DeribitRecoveryQueryException(DeribitRecoveryQueryProblem.Protocol);
        if (root.TryGetProperty("error", out var error))
        {
            int? code = error.TryGetProperty("code", out var raw) && raw.TryGetInt32(out var number) ? number : null;
            throw new DeribitRecoveryQueryException(DeribitRecoveryQueryProblem.Protocol, (int)response.StatusCode, code);
        }
        if (!response.IsSuccessStatusCode) throw new DeribitRecoveryQueryException(DeribitRecoveryQueryProblem.Transport, (int)response.StatusCode);
        return root.GetProperty("result").Clone();
    }
}
