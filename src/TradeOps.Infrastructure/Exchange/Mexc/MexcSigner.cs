using System.Security.Cryptography;
using System.Text;

namespace TradeOps.Infrastructure.Exchange.Mexc;

internal static class MexcSigner
{
    public static string BuildQueryString(
        IEnumerable<KeyValuePair<string, string?>> parameters)
    {
        return string.Join(
            "&",
            parameters
                .Where(item => item.Value is not null)
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item =>
                    $"{item.Key}={Uri.EscapeDataString(item.Value ?? string.Empty)}"));
    }

    public static string Sign(
        string accessKey,
        long requestTime,
        string requestParameterString,
        string secretKey)
    {
        if (string.IsNullOrWhiteSpace(accessKey)
            || string.IsNullOrWhiteSpace(secretKey))
        {
            throw new InvalidOperationException(
                "MEXC API credentials are not configured.");
        }

        var target =
            accessKey
            + requestTime.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + requestParameterString;

        using var hmac = new HMACSHA256(
            Encoding.UTF8.GetBytes(secretKey));

        return Convert.ToHexString(
                hmac.ComputeHash(Encoding.UTF8.GetBytes(target)))
            .ToLowerInvariant();
    }
}
