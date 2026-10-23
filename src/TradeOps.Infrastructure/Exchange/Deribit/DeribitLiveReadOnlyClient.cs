using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TradeOps.Infrastructure.Exchange.Deribit;

/// <summary>
/// Isolated production-account probe. NOT an IExchangeClient; it has no
/// place, edit, cancel, transfer or withdrawal method and is never DI-wired.
/// HTTP requests use a closed set of three private GET endpoints.
/// </summary>
public sealed class DeribitLiveReadOnlyClient(HttpClient httpClient,
    string clientId, string clientSecret)
{
    public const string ProductionOrigin = "https://www.deribit.com";
    private static readonly Uri SummariesUri =
        new("https://www.deribit.com/api/v2/private/get_account_summaries");
    private static readonly Uri PositionsUri =
        new("https://www.deribit.com/api/v2/private/get_positions?currency=any");
    private static readonly Uri OrdersUri =
        new("https://www.deribit.com/api/v2/private/get_open_orders");

    public async Task<DeribitLiveReadOnlyCounts> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        // Validate before first network request. Nothing is ever sent
        // to a caller-controlled host, not even when HttpClient.BaseAddress
        // has been configured externally.
        if (string.IsNullOrWhiteSpace(clientId)
            || string.IsNullOrWhiteSpace(clientSecret)
            || clientId.Contains(':')
            || clientId.Any(char.IsControl)
            || clientSecret.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                "A separate, valid Deribit production READ-ONLY API key is required.");
        }

        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(clientId + ":" + clientSecret));

        var summaries = await FetchAsync(SummariesUri, credentials,
            cancellationToken);
        if (summaries.ValueKind != JsonValueKind.Object
            || !summaries.TryGetProperty("summaries", out var currencies)
            || currencies.ValueKind != JsonValueKind.Array)
        {
            throw Invalid();
        }

        foreach (var account in currencies.EnumerateArray())
        {
            if (account.ValueKind != JsonValueKind.Object
                || !account.TryGetProperty("currency", out var currency)
                || currency.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(currency.GetString()))
            {
                throw Invalid();
            }
        }

        var positions = await FetchAsync(PositionsUri, credentials,
            cancellationToken);
        if (positions.ValueKind != JsonValueKind.Array
            || positions.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.Object))
        {
            throw Invalid();
        }

        var orders = await FetchAsync(OrdersUri, credentials,
            cancellationToken);
        if (orders.ValueKind != JsonValueKind.Array
            || orders.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.Object))
        {
            throw Invalid();
        }

        // Counts only. No balance, account ID, position symbol, order ID,
        // wallet address, access token or raw exchange body is logged.
        return new DeribitLiveReadOnlyCounts(
            currencies.GetArrayLength(),
            positions.GetArrayLength(),
            orders.GetArrayLength());
    }

    private async Task<JsonElement> FetchAsync(Uri uri, string credentials,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps,
                StringComparison.Ordinal)
            || !string.Equals(uri.Host, "www.deribit.com",
                StringComparison.Ordinal)
            || uri.Port != 443
            || uri.UserInfo.Length != 0
            || uri.Fragment.Length != 0
            || !new[] { SummariesUri, PositionsUri, OrdersUri }
                .Contains(uri))
        {
            throw Invalid();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Basic", credentials);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        // Never follow HTTP redirects: the executable HttpClient must have
        // AllowAutoRedirect=false, and this also refuses redirect responses.
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Deribit read-only HTTP request failed with status {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream,
            new JsonDocumentOptions { MaxDepth = 32 },
            cancellationToken);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("jsonrpc", out var version)
            || version.GetString() != "2.0"
            || root.TryGetProperty("error", out var error)
                && error.ValueKind != JsonValueKind.Null
            || !root.TryGetProperty("result", out var result)
            || result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw Invalid();
        }

        return result.Clone();
    }

    private static InvalidOperationException Invalid() =>
        new("Deribit read-only response shape or method boundary invalid; no state was changed.");
}

public sealed record DeribitLiveReadOnlyCounts(
    int AccountCurrencies, int Positions, int OpenOrders);
