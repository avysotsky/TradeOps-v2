using Microsoft.AspNetCore.Mvc;

namespace TradeOps.Api.Integrations.TradingView;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class TradingViewGatewayAttribute : TypeFilterAttribute
{
    public TradingViewGatewayAttribute()
        : base(typeof(TradingViewGatewayAuthorizationFilter))
    {
    }
}
