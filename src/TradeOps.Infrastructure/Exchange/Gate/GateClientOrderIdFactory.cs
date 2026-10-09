using System.Security.Cryptography;
using System.Text;

namespace TradeOps.Infrastructure.Exchange.Gate;

internal static class GateClientOrderIdFactory
{
    private const string Prefix = "TradeOps:Gate:";

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

        // Gate custom order text must start with t- and the user part
        // is limited to 28 bytes. 26 hex chars keeps the complete value compact.
        return "t-" + Convert.ToHexString(
                hash.AsSpan(0, 13))
            .ToLowerInvariant();
    }
}
