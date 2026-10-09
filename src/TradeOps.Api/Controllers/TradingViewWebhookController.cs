using Microsoft.AspNetCore.Mvc;
using TradeOps.Api.Contracts;
using TradeOps.Api.Integrations.TradingView;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Api.Controllers;

[ApiController]
[TradingViewGateway]
[Route("api/integrations/tradingview")]
public sealed class TradingViewWebhookController(
    ISignalExecutionService signalExecutionService,
    IOperatorReadRepository operatorReadRepository,
    ITradingViewDeliveryAuditRepository deliveryAuditRepository) : ControllerBase
{
    private const int MaxEventIdLength = 200;

    [HttpPost]
    [ProducesResponseType<TradingViewWebhookResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<TradingViewWebhookResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<SignalIdConflictResponse>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TradingViewWebhookResponse>> Post(
        TradingViewWebhookRequest request,
        CancellationToken cancellationToken)
    {
        var receivedAt = DateTimeOffset.UtcNow;
        var deliveryId = await deliveryAuditRepository.StartAsync(
            request.EventId,
            request.Symbol,
            request.Action,
            receivedAt,
            cancellationToken);

        Response.Headers["X-TradeOps-TradingView-Delivery-Id"] =
            deliveryId.ToString("D");

        try
        {
            var validationErrors = ValidateAdapterRequest(request);
            if (validationErrors.Count > 0)
            {
                await deliveryAuditRepository.CompleteAsync(
                    deliveryId,
                    TradingViewDeliveryOutcome.ValidationRejected,
                    StatusCodes.Status400BadRequest,
                    cancellationToken: cancellationToken);

                return BadRequest(new ValidationProblemDetails(validationErrors)
                {
                    Title = "TradingView webhook validation failed.",
                    Status = StatusCodes.Status400BadRequest
                });
            }

            var side = ParseAction(request.Action);
            var signalId = TradingViewSignalIdFactory.Create(
                request.EventId);

            var canonicalRequest = new CreateTradingSignalRequest(
                request.Symbol,
                side,
                request.Quantity,
                request.RiskPercent,
                request.StopLoss,
                request.TakeProfit,
                Source: "TradingView",
                SignalId: signalId);

            var canonicalValidationErrors =
                TradingSignalRequestValidator.Validate(
                    canonicalRequest);

            if (canonicalValidationErrors.Count > 0)
            {
                await deliveryAuditRepository.CompleteAsync(
                    deliveryId,
                    TradingViewDeliveryOutcome.ValidationRejected,
                    StatusCodes.Status400BadRequest,
                    signalId,
                    cancellationToken: cancellationToken);

                return BadRequest(new ValidationProblemDetails(
                    canonicalValidationErrors)
                {
                    Title = "TradingView webhook validation failed.",
                    Status = StatusCodes.Status400BadRequest
                });
            }

            var existingSignal = await operatorReadRepository
                .GetSignalByIdAsync(
                    signalId,
                    cancellationToken);

            var signal = new TradingSignal
            {
                Id = signalId,
                Symbol = canonicalRequest.Symbol.Trim().ToUpperInvariant(),
                Side = canonicalRequest.Side,
                SignalType = "TradingView",
                RequestedQuantity = canonicalRequest.Quantity,
                RiskPercent = canonicalRequest.RiskPercent,
                StopLoss = canonicalRequest.StopLoss,
                TakeProfit = canonicalRequest.TakeProfit,
                CreatedAt = receivedAt,
                Source = "TradingView"
            };

            SignalExecutionResult result;
            try
            {
                result = await signalExecutionService.ExecuteSignalAsync(
                    signal,
                    cancellationToken);
            }
            catch (SignalIdConflictException exception)
            {
                var persisted = await operatorReadRepository
                    .GetSignalByIdAsync(
                        exception.SignalId,
                        cancellationToken);

                await deliveryAuditRepository.CompleteAsync(
                    deliveryId,
                    TradingViewDeliveryOutcome.Conflict,
                    StatusCodes.Status409Conflict,
                    exception.SignalId,
                    persisted?.OrderId,
                    persisted?.ClientOrderId,
                    cancellationToken);

                return Conflict(new SignalIdConflictResponse(
                    exception.SignalId,
                    exception.Message,
                    exception.ConflictingFields));
            }
            catch (ClientOrderIdConflictException exception)
            {
                var persisted = await operatorReadRepository
                    .GetSignalByIdAsync(
                        exception.SignalId,
                        cancellationToken);

                await deliveryAuditRepository.CompleteAsync(
                    deliveryId,
                    TradingViewDeliveryOutcome.Conflict,
                    StatusCodes.Status409Conflict,
                    exception.SignalId,
                    persisted?.OrderId,
                    persisted?.ClientOrderId,
                    cancellationToken);

                return Conflict(new SignalIdConflictResponse(
                    exception.SignalId,
                    exception.Message,
                    exception.ConflictingFields));
            }

            var persistedSignal = await operatorReadRepository
                .GetSignalByIdAsync(
                    result.SignalId,
                    cancellationToken);

            var responseStatus = result.Accepted
                ? StatusCodes.Status200OK
                : StatusCodes.Status422UnprocessableEntity;

            var deliveryOutcome = result.Accepted
                ? existingSignal is null
                    ? TradingViewDeliveryOutcome.Accepted
                    : TradingViewDeliveryOutcome.Redelivered
                : TradingViewDeliveryOutcome.RiskRejected;

            await deliveryAuditRepository.CompleteAsync(
                deliveryId,
                deliveryOutcome,
                responseStatus,
                result.SignalId,
                persistedSignal?.OrderId,
                persistedSignal?.ClientOrderId
                    ?? result.Order?.ClientOrderId,
                cancellationToken);

            var response = new TradingViewWebhookResponse(
                request.EventId.Trim(),
                result.SignalId,
                result.Accepted,
                result.RiskReasons,
                result.Order);

            return result.Accepted
                ? Ok(response)
                : UnprocessableEntity(response);
        }
        catch
        {
            await deliveryAuditRepository.CompleteAsync(
                deliveryId,
                TradingViewDeliveryOutcome.Failed,
                StatusCodes.Status500InternalServerError,
                cancellationToken: CancellationToken.None);

            throw;
        }
    }

    private static Dictionary<string, string[]> ValidateAdapterRequest(
        TradingViewWebhookRequest request)
    {
        var errors = new Dictionary<string, string[]>(
            StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(request.EventId))
        {
            errors[nameof(request.EventId)] =
                ["EventId is required."];
        }
        else if (request.EventId.Trim().Length > MaxEventIdLength)
        {
            errors[nameof(request.EventId)] =
                [$"EventId must not exceed {MaxEventIdLength} characters."];
        }

        if (!TryParseAction(request.Action, out _))
        {
            errors[nameof(request.Action)] =
                ["Action must be 'buy' or 'sell'."];
        }

        return errors;
    }

    private static OrderSide ParseAction(string action)
    {
        _ = TryParseAction(action, out var side);
        return side;
    }

    private static bool TryParseAction(
        string? action,
        out OrderSide side)
    {
        if (string.Equals(
                action?.Trim(),
                "buy",
                StringComparison.OrdinalIgnoreCase))
        {
            side = OrderSide.Buy;
            return true;
        }

        if (string.Equals(
                action?.Trim(),
                "sell",
                StringComparison.OrdinalIgnoreCase))
        {
            side = OrderSide.Sell;
            return true;
        }

        side = default;
        return false;
    }
}
