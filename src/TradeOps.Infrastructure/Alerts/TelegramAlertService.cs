using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;

namespace TradeOps.Infrastructure.Alerts;

public sealed class TelegramAlertService(
    IHttpClientFactory httpClientFactory,
    IOptions<TelegramOptions> options,
    ILogger<TelegramAlertService> logger) : IAlertService
{
    public async Task SendAsync(
        AlertMessage alert,
        CancellationToken cancellationToken = default)
    {
        var settings = options.Value;

        if (!settings.Enabled)
        {
            logger.LogDebug(
                "Telegram alert {EventType} suppressed because Telegram alerts are disabled.",
                alert.EventType);
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.BotToken)
            || string.IsNullOrWhiteSpace(settings.ChatId))
        {
            logger.LogWarning(
                "Telegram alert {EventType} was not sent because BotToken or ChatId is not configured.",
                alert.EventType);
            return;
        }

        var text = $"[{alert.Severity}] {alert.EventType}\n{alert.Message}";

        if (!string.IsNullOrWhiteSpace(alert.CorrelationId))
        {
            text += $"\nCorrelation: {alert.CorrelationId}";
        }

        var endpoint = $"https://api.telegram.org/bot{settings.BotToken}/sendMessage";
        var payload = new
        {
            chat_id = settings.ChatId,
            text,
            disable_web_page_preview = true
        };

        try
        {
            var client = httpClientFactory.CreateClient("telegram");
            using var response = await client.PostAsJsonAsync(
                endpoint,
                payload,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Telegram alert {EventType} failed with HTTP status {StatusCode}.",
                    alert.EventType,
                    (int)response.StatusCode);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Telegram alert {EventType} failed. Trading flow will continue.",
                alert.EventType);
        }
    }
}
