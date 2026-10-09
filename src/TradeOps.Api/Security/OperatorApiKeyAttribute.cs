using Microsoft.AspNetCore.Mvc;

namespace TradeOps.Api.Security;

[AttributeUsage(AttributeTargets.Method)]
public sealed class OperatorApiKeyAttribute : TypeFilterAttribute
{
    public OperatorApiKeyAttribute()
        : base(typeof(OperatorApiKeyFilter))
    {
    }
}
