using System.Security.Cryptography;
using System.Text;

namespace TradeOps.Infrastructure.Exchange.Coinbase;

internal static class CoinbaseIntxSigner
{
    public static string Sign(
        string timestamp,
        string method,
        string requestPath,
        string body,
        string base64SigningKey)
    {
        if (string.IsNullOrWhiteSpace(base64SigningKey))
        {
            throw new InvalidOperationException(
                "Coinbase INTX signing key is not configured.");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64SigningKey);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "Coinbase INTX signing key must be Base64 encoded.",
                exception);
        }

        var message =
            timestamp
            + method.ToUpperInvariant()
            + requestPath
            + body;

        using var hmac = new HMACSHA256(key);

        return Convert.ToBase64String(
            hmac.ComputeHash(
                Encoding.UTF8.GetBytes(message)));
    }
}
