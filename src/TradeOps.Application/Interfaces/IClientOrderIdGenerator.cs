namespace TradeOps.Application.Interfaces;

public interface IClientOrderIdGenerator
{
    string Generate(Guid signalId);
}
