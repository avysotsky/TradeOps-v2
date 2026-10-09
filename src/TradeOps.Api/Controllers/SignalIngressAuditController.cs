using Microsoft.AspNetCore.Mvc;
using TradeOps.Api.Contracts;
using TradeOps.Application.Interfaces;
using TradeOps.Domain.Entities;

namespace TradeOps.Api.Controllers;

[ApiController]
[Route("api/signal-ingress/requests")]
public sealed class SignalIngressAuditController(
    ISignalIngressRequestAuditRepository auditRepository) : ControllerBase
{
    [HttpGet("{requestId:guid}")]
    [ProducesResponseType<IReadOnlyCollection<SignalIngressRequestAuditResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyCollection<SignalIngressRequestAuditResponse>>> GetByRequestId(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var attempts = await auditRepository.GetByRequestIdAsync(
            requestId,
            cancellationToken);

        if (attempts.Count == 0)
        {
            return NotFound();
        }

        return Ok(attempts.Select(ToResponse).ToArray());
    }

    private static SignalIngressRequestAuditResponse ToResponse(
        SignalIngressRequestAudit item)
    {
        return new SignalIngressRequestAuditResponse(
            item.Id,
            item.RequestId,
            item.RequestTimestamp,
            item.ReceivedAt,
            item.CompletedAt,
            item.Method,
            item.Path,
            item.Outcome,
            item.HttpStatusCode,
            item.SignalId,
            item.OrderId,
            item.ClientOrderId);
    }
}
