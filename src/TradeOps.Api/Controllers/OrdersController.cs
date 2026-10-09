using Microsoft.AspNetCore.Mvc;
using TradeOps.Api.Contracts;
using TradeOps.Api.Security;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Api.Controllers;

[ApiController]
[Route("api/orders")]
public sealed class OrdersController(
    IExchangeClient exchangeClient,
    IOperatorReadRepository operatorReadRepository,
    ILocalOrderAuditRepository localOrderAuditRepository,
    IOrderCancellationService orderCancellationService,
    IOrderBulkCancellationService orderBulkCancellationService) : ControllerBase
{
    private const int MaxAuditLimit = 200;
    private const int MaxSymbolLength = 50;

    [HttpGet]
    [ProducesResponseType<IReadOnlyCollection<Order>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<Order>>> GetOpenOrders(
        CancellationToken cancellationToken)
    {
        var orders = await exchangeClient.GetOpenOrdersAsync(cancellationToken);
        return Ok(orders);
    }

    [HttpGet("local")]
    [ProducesResponseType<IReadOnlyCollection<LocalOrderResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyCollection<LocalOrderResponse>>> GetLocalOrders(
        [FromQuery] string? symbol = null,
        [FromQuery] OrderStatus? status = null,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > MaxAuditLimit)
        {
            return Problem(
                title: $"Local order audit limit must be between 1 and {MaxAuditLimit}.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        string? normalizedSymbol = null;
        if (!string.IsNullOrWhiteSpace(symbol))
        {
            normalizedSymbol = symbol.Trim().ToUpperInvariant();
            if (normalizedSymbol.Length > MaxSymbolLength)
            {
                return Problem(
                    title: $"Order symbol must not exceed {MaxSymbolLength} characters.",
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }

        var fromInclusive = from?.ToUniversalTime();
        var toExclusive = to?.ToUniversalTime();
        if (fromInclusive.HasValue &&
            toExclusive.HasValue &&
            fromInclusive.Value >= toExclusive.Value)
        {
            return Problem(
                title: "Local order audit 'from' must be earlier than 'to'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var orders = await localOrderAuditRepository.GetAsync(
            normalizedSymbol,
            status,
            fromInclusive,
            toExclusive,
            limit,
            cancellationToken);

        return Ok(orders.Select(OperatorResponseMapper.ToLocalOrder).ToArray());
    }

    [HttpGet("local/{idOrClientOrderId}")]
    [OperatorApiKey]
    [ProducesResponseType<LocalOrderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LocalOrderResponse>> GetLocalOrder(
        string idOrClientOrderId,
        CancellationToken cancellationToken)
    {
        var order = await operatorReadRepository.GetLocalOrderAsync(
            idOrClientOrderId,
            cancellationToken);

        return order is null
            ? NotFound()
            : Ok(OperatorResponseMapper.ToLocalOrder(order));
    }

    [HttpGet("local/{idOrClientOrderId}/history")]
    [OperatorApiKey]
    [ProducesResponseType<IReadOnlyCollection<OrderLifecycleEventResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyCollection<OrderLifecycleEventResponse>>> GetLocalOrderHistory(
        string idOrClientOrderId,
        CancellationToken cancellationToken)
    {
        var history = await operatorReadRepository.GetOrderHistoryAsync(
            idOrClientOrderId,
            cancellationToken);

        if (history.Count == 0)
        {
            return NotFound();
        }

        return Ok(history.Select(item => new OrderLifecycleEventResponse(
            item.Id,
            item.OrderId,
            item.ClientOrderId,
            item.PreviousStatus,
            item.Status,
            item.FilledQuantity,
            item.AverageFillPrice,
            item.ExchangeOrderId,
            item.Source,
            item.OccurredAt)).ToArray());
    }

    [HttpPost("local/{idOrClientOrderId}/cancel")]
    [OperatorApiKey]
    [ProducesResponseType<OrderCancellationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<OrderCancellationResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<OrderCancellationResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<OrderCancellationResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<OrderCancellationResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<OrderCancellationResponse>> CancelLocalOrder(
        string idOrClientOrderId,
        CancellationToken cancellationToken)
    {
        var result = await orderCancellationService.CancelAsync(
            idOrClientOrderId,
            cancellationToken);
        var response = OperatorResponseMapper.ToOrderCancellation(result);

        return result.Outcome switch
        {
            OrderCancellationOutcome.NotFound => NotFound(response),
            OrderCancellationOutcome.NotCancellable => Conflict(response),
            OrderCancellationOutcome.Unresolved =>
                StatusCode(StatusCodes.Status503ServiceUnavailable, response),
            OrderCancellationOutcome.CancellationRequested => Accepted(response),
            _ => Ok(response)
        };
    }

    [HttpPost("local/cancel-all")]
    [OperatorApiKey]
    [ProducesResponseType<BulkOrderCancellationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<BulkOrderCancellationResponse>> CancelAllLocalOrders(
        [FromQuery] string? symbol,
        CancellationToken cancellationToken)
    {
        var result = await orderBulkCancellationService.CancelOpenOrdersAsync(
            symbol,
            cancellationToken);

        return Ok(OperatorResponseMapper.ToBulkOrderCancellation(result));
    }

    [HttpGet("{exchangeOrderId}")]
    [ProducesResponseType<Order>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Order>> GetByExchangeOrderId(
        string exchangeOrderId,
        [FromQuery] string? symbol,
        CancellationToken cancellationToken)
    {
        var order = await exchangeClient.GetOrderAsync(
            exchangeOrderId,
            symbol,
            cancellationToken);
        return order is null ? NotFound() : Ok(order);
    }

    [HttpGet("by-client/{clientOrderId}")]
    [ProducesResponseType<Order>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Order>> GetByClientOrderId(
        string clientOrderId,
        [FromQuery] string? symbol,
        CancellationToken cancellationToken)
    {
        var order = await exchangeClient.GetOrderByClientOrderIdAsync(
            clientOrderId,
            symbol,
            cancellationToken);

        return order is null ? NotFound() : Ok(order);
    }

    [HttpDelete("{exchangeOrderId}")]
    [OperatorApiKey]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(
        string exchangeOrderId,
        [FromQuery] string? symbol,
        CancellationToken cancellationToken)
    {
        var order = await exchangeClient.GetOrderAsync(
            exchangeOrderId,
            symbol,
            cancellationToken);
        if (order is null)
        {
            return NotFound();
        }

        if (order.Status is OrderStatus.Filled
            or OrderStatus.Cancelled
            or OrderStatus.Rejected)
        {
            return Conflict(new
            {
                message = $"Order '{exchangeOrderId}' is already terminal ({order.Status}) and cannot be cancelled."
            });
        }

        await exchangeClient.CancelOrderAsync(
            exchangeOrderId,
            symbol,
            cancellationToken);
        return Accepted();
    }
}
