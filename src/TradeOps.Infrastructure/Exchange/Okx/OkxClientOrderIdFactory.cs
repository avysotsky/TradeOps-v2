using System.Security.Cryptography;
using System.Text;

namespace TradeOps.Infrastructure.Exchange.Okx;

internal static class OkxClientOrderIdFactory
{
    private const string Prefix = "TradeOps:OKX:";

    public static string Create(string clientOrderId)
    {
        if (string.IsNullOrWhiteSpace(clientOrderId))
        {
            throw new ArgumentException(
                "ClientOrderId is required.",
                nameof(clientOrderId));
        }

        var hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(
                Prefix + clientOrderId.Trim()));

        return Convert.ToHexString(
                hash.AsSpan(0, 16))
            .ToLowerInvariant();
    }
}
