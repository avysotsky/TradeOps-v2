using System.Security.Cryptography;
using System.Text;

namespace TradeOps.Infrastructure.Exchange.KuCoin;

internal static class KuCoinSigner
{
    public static string Sign(
        string timestamp,
        string method,
        string endpointWithQuery,
        string body,
        string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException(
                "KuCoin API secret is not configured.");
        }

        var prehash =
            timestamp
            + method.ToUpperInvariant()
            + endpointWithQuery
            + body;

        using var hmac = new HMACSHA256(
            Encoding.UTF8.GetBytes(secret));

        return Convert.ToBase64String(
            hmac.ComputeHash(
                Encoding.UTF8.GetBytes(prehash)));
    }

    public static string EncryptPassphrase(
        string passphrase,
        string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException(
                "KuCoin API secret is not configured.");
        }

        using var hmac = new HMACSHA256(
            Encoding.UTF8.GetBytes(secret));

        return Convert.ToBase64String(
            hmac.ComputeHash(
                Encoding.UTF8.GetBytes(passphrase)));
    }
}
