using TradeOps.Application.Interfaces;

namespace TradeOps.Application.Services;

public sealed class ClientOrderIdGenerator : IClientOrderIdGenerator
{
    // 36 characters exactly: Bybit orderLinkId allows a maximum of 36.
    public string Generate(Guid signalId) => $"trd-{signalId:N}";
}
