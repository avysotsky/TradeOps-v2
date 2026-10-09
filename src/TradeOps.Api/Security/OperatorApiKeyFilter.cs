using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace TradeOps.Api.Security;

public sealed class OperatorApiKeyFilter(
    IOptions<OperatorApiAuthOptions> options) : IAsyncAuthorizationFilter
{
    private readonly OperatorApiAuthOptions _options = options.Value;

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (!_options.Enabled)
        {
            return Task.CompletedTask;
        }

        var request = context.HttpContext.Request;
        if (!request.Headers.TryGetValue(_options.HeaderName, out var suppliedKeys)
            || suppliedKeys.Count != 1
            || string.IsNullOrEmpty(suppliedKeys[0])
            || !ApiKeyVerifier.Matches(_options.ApiKey, suppliedKeys[0]!))
        {
            context.HttpContext.Response.Headers["WWW-Authenticate"] = "ApiKey";
            context.Result = new ObjectResult(new ProblemDetails
            {
                Type = "https://httpstatuses.com/401",
                Title = "Operator API authentication failed.",
                Status = StatusCodes.Status401Unauthorized
            })
            {
                StatusCode = StatusCodes.Status401Unauthorized
            };
        }

        return Task.CompletedTask;
    }
}
