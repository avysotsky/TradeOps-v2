using System.Security.Cryptography;
using System.Text;

namespace TradeOps.Infrastructure.Exchange.Kraken;

internal static class KrakenFuturesSigner
{
    public static string Sign(
        string encodedPostData,
        string nonce,
        string endpointPath,
        string base64Secret)
    {
        if (string.IsNullOrWhiteSpace(base64Secret))
        {
            throw new InvalidOperationException(
                "Kraken Futures API secret is not configured.");
        }

        var shaInput = Encoding.UTF8.GetBytes(
            encodedPostData + nonce + endpointPath);

        var sha256 = SHA256.HashData(shaInput);
        var secret = Convert.FromBase64String(base64Secret);

        using var hmac = new HMACSHA512(secret);
        return Convert.ToBase64String(hmac.ComputeHash(sha256));
    }
}
