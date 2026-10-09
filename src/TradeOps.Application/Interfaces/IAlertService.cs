using TradeOps.Application.Models;

namespace TradeOps.Application.Interfaces;

public interface IAlertService
{
    Task SendAsync(
        AlertMessage alert,
        CancellationToken cancellationToken = default);
}
