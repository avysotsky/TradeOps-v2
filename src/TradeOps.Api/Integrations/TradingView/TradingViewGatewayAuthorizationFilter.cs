using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using TradeOps.Api.Security;

namespace TradeOps.Api.Integrations.TradingView;

public sealed class TradingViewGatewayAuthorizationFilter(
    IOptions<TradingViewWebhookOptions> options) : IAsyncAuthorizationFilter
{
    private readonly TradingViewWebhookOptions _options = options.Value;

    public Task OnAuthorizationAsync(
        AuthorizationFilterContext context)
    {
        if (!_options.Enabled)
        {
            context.Result = new NotFoundResult();
            return Task.CompletedTask;
        }

        var request = context.HttpContext.Request;

        if (!request.Headers.TryGetValue(
                _options.GatewayHeaderName,
                out var values)
            || values.Count != 1
            || string.IsNullOrWhiteSpace(values[0])
            || !ApiKeyVerifier.Matches(
                _options.GatewayKey,
                values[0]!))
        {
            context.HttpContext.Response.Headers[
                "WWW-Authenticate"] = "GatewayApiKey";

            context.Result = Problem(
                StatusCodes.Status401Unauthorized,
                "TradingView gateway authentication failed.");
            return Task.CompletedTask;
        }

        if (_options.RequireGatewayIpAllowlist
            && !IsAllowedGatewayIp(
                context.HttpContext.Connection.RemoteIpAddress))
        {
            context.Result = Problem(
                StatusCodes.Status403Forbidden,
                "TradingView gateway source IP is not allowed.");
        }

        return Task.CompletedTask;
    }

    private bool IsAllowedGatewayIp(IPAddress? remoteIp)
    {
        if (remoteIp is null)
        {
            return false;
        }

        foreach (var configured in _options.AllowedGatewayIps)
        {
            if (!IPAddress.TryParse(configured, out var allowed))
            {
                continue;
            }

            if (remoteIp.Equals(allowed)
                || remoteIp.MapToIPv6().Equals(
                    allowed.MapToIPv6()))
            {
                return true;
            }
        }

        return false;
    }

    private static ObjectResult Problem(
        int statusCode,
        string title)
    {
        return new ObjectResult(new ProblemDetails
        {
            Type = $"https://httpstatuses.com/{statusCode}",
            Title = title,
            Status = statusCode
        })
        {
            StatusCode = statusCode
        };
    }
}
