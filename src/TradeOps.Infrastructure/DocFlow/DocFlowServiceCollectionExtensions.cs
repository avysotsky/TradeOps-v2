using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradeOps.Application.Models;

namespace TradeOps.Infrastructure.DocFlow;

public static class DocFlowServiceCollectionExtensions
{
    /// <summary>Explicit opt-in: no outbound DocFlow calls until configured and injected.</summary>
    public static IServiceCollection AddTradeOpsDocFlowHttp(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var endpoint = configuration["DocFlow:BaseAddress"];
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new InvalidOperationException("DocFlow:BaseAddress is required.");

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var baseAddress))
            throw new InvalidOperationException("DocFlow:BaseAddress must be an absolute URI.");

        services.AddHttpClient<IDocFlowExtractionPort, DocFlowHttpExtractionAdapter>(client =>
        {
            client.BaseAddress = new Uri(baseAddress.AbsoluteUri.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false
        });
        return services;
    }
}
