namespace TradeOps.Application.Models;

public sealed class ClientOrderIdConflictException : InvalidOperationException
{
    public ClientOrderIdConflictException(
        Guid signalId,
        string clientOrderId,
        Guid existingOrderId,
        IReadOnlyCollection<string> conflictingFields)
        : base(
            $"ClientOrderId '{clientOrderId}' for signal '{signalId}' is already bound to a different local order payload. " +
            $"Existing order: '{existingOrderId}'. Conflicting fields: {string.Join(", ", conflictingFields)}.")
    {
        SignalId = signalId;
        ClientOrderId = clientOrderId;
        ExistingOrderId = existingOrderId;
        ConflictingFields = conflictingFields.ToArray();
    }

    public Guid SignalId { get; }

    public string ClientOrderId { get; }

    public Guid ExistingOrderId { get; }

    public IReadOnlyCollection<string> ConflictingFields { get; }
}
