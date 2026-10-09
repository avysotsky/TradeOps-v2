namespace TradeOps.Application.Models;

public sealed record TargetPosition(
    InstrumentReference Instrument,
    decimal TargetWeight,
    decimal ReferencePrice,
    decimal TargetNotional,
    decimal TargetQuantity);
