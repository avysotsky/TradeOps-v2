using System.Security.Cryptography;
using System.Text;

namespace TradeOps.Api.Integrations.TradingView;

internal static class TradingViewSignalIdFactory
{
    private const string NamespacePrefix =
        "TradeOps:TradingView:";

    public static Guid Create(string eventId)
    {
        var input = Encoding.UTF8.GetBytes(
            NamespacePrefix + eventId.Trim());

        var hash = SHA256.HashData(input);
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);

        return new Guid(guidBytes);
    }
}
