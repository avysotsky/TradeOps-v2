using TradeOps.Application.Models;

namespace TradeOps.Application.Interfaces;

public interface IExchangeCapabilityCatalog
{
    ExchangeCapabilityProfile Current { get; }

    IReadOnlyCollection<ExchangeCapabilityProfile> All { get; }

    ExchangeCapabilityProfile Get(string provider);
}
