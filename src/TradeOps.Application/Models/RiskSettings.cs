namespace TradeOps.Application.Models;

public sealed class RiskSettings
{
    public decimal MaxPositionSize { get; init; } = 1m;

    public decimal MaxOrderSize { get; init; } = 0.10m;

    public decimal MaxDailyLoss { get; init; } = 500m;

    public int MaxOpenPositions { get; init; } = 5;

    public HashSet<string> AllowedSymbols { get; init; } =
        new(StringComparer.OrdinalIgnoreCase) { "BTCUSDT", "ETHUSDT" };

    public bool TradingEnabled { get; set; } = true;

    public bool EmergencyStop { get; set; }
}
