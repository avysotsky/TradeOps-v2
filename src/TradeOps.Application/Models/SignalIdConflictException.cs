namespace TradeOps.Application.Models;

public sealed class SignalIdConflictException : InvalidOperationException
{
    public SignalIdConflictException(
        Guid signalId,
        IReadOnlyCollection<string> conflictingFields)
        : base(
            $"SignalId '{signalId}' is already bound to a different execution payload. " +
            $"Conflicting fields: {string.Join(", ", conflictingFields)}.")
    {
        SignalId = signalId;
        ConflictingFields = conflictingFields.ToArray();
    }

    public Guid SignalId { get; }

    public IReadOnlyCollection<string> ConflictingFields { get; }
}
