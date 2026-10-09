using TradeOps.Application.Interfaces;

namespace TradeOps.Infrastructure.Risk;

public sealed class InMemoryRiskState : IRiskState
{
    public decimal CurrentDailyPnl { get; private set; }

    public void SetCurrentDailyPnl(decimal value)
    {
        CurrentDailyPnl = value;
    }
}
