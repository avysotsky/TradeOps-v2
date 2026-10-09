using System.Security.Cryptography;
using System.Text;

namespace TradeOps.Infrastructure.Exchange.Gate;

internal static class GateSigner
{
    public static string Sign(
        string method,
        string requestPath,
        string query,
        string body,
        long timestampSeconds,
        string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException(
                "Gate API secret is not configured.");
        }

        var bodyHash = Convert.ToHexString(
                SHA512.HashData(
                    Encoding.UTF8.GetBytes(body)))
            .ToLowerInvariant();

        var payload = string.Join(
            "\n",
            method.ToUpperInvariant(),
            requestPath,
            query,
            bodyHash,
            timestampSeconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture));

        using var hmac = new HMACSHA512(
            Encoding.UTF8.GetBytes(secret));

        return Convert.ToHexString(
                hmac.ComputeHash(
                    Encoding.UTF8.GetBytes(payload)))
            .ToLowerInvariant();
    }
}
