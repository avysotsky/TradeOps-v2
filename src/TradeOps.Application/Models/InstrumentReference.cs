using TradeOps.Domain.Enums;

namespace TradeOps.Application.Models;

public sealed record InstrumentReference(
    string Symbol,
    AssetClass AssetClass,
    string? Currency = null,
    string? VenueInstrumentId = null,
    string? Exchange = null);
