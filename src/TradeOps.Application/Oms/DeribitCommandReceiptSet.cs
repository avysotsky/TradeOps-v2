using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

/// <summary>Validate actual receipt evidence, never a caller-provided completeness/mapping flag.</summary>
public static class DeribitCommandReceiptSet
{
    public static void Validate(AccountKey scope, IReadOnlyList<OmsRecoveryCommand> commands,
        IReadOnlyList<DeribitJsonRpcCommandReceipt> receipts, DateTimeOffset readAtUtc)
    {
        if (receipts.Count > 10000) throw new ArgumentException("Receipt capacity exceeded.");
        var byCommand = commands.ToDictionary(x => x.Request.CommandId);
        var seenCommands = new HashSet<OmsCommandId>();
        var seenRequests = new HashSet<(Guid, long)>();
        var placements = new Dictionary<(string, string), DeribitJsonRpcCommandReceipt>();
        foreach (var receipt in receipts)
        {
            receipt.Validate();
            if (receipt.Command.Scope != scope || receipt.ReceivedAtUtc > readAtUtc.AddSeconds(5) ||
                !byCommand.TryGetValue(receipt.Command.CommandId, out var command) || command.Request != receipt.Command ||
                command.Phase is OmsRecoveryDispatchPhase.PersistedNotSent or OmsRecoveryDispatchPhase.Rejected ||
                !seenCommands.Add(receipt.Command.CommandId) || !seenRequests.Add((receipt.SessionId, receipt.RpcRequestId)))
                throw new ArgumentException("Invalid receipt snapshot binding.");
            if (receipt.Command.Payload.Kind == OmsJournalCommandKind.Place &&
                !placements.TryAdd((receipt.Order.Currency, receipt.Order.ExchangeOrderId), receipt))
                throw new ArgumentException("Native placement identity collision.");
        }
        foreach (var receipt in receipts.Where(x => x.Command.Payload.Kind == OmsJournalCommandKind.Cancel))
        {
            if (!placements.TryGetValue((receipt.Order.Currency, receipt.Order.ExchangeOrderId), out var placement))
                throw new ArgumentException("Cancel lacks placement binding.");
            ValidateCancelBinding(receipt, placement);
        }
    }

    public static void ValidateCancelBinding(DeribitJsonRpcCommandReceipt receipt, DeribitJsonRpcCommandReceipt placement)
    {
        receipt.Validate(); placement.Validate();
        if (receipt.Command.Payload.Kind != OmsJournalCommandKind.Cancel ||
                placement.Command.Payload.Kind != OmsJournalCommandKind.Place ||
                receipt.Command.Scope != placement.Command.Scope || receipt.Order.Currency != placement.Order.Currency ||
                receipt.Order.ExchangeOrderId != placement.Order.ExchangeOrderId ||
                placement.Command.LocalOrderId != receipt.Command.LocalOrderId ||
                placement.Command.ExpectedAggregateVersion.Value >= receipt.Command.ExpectedAggregateVersion.Value ||
                placement.Order.NativeInstrument != receipt.Order.NativeInstrument ||
                placement.Order.Side != receipt.Order.Side || placement.Order.NativeAmount != receipt.Order.NativeAmount ||
                placement.Order.Price != receipt.Order.Price || placement.PostOnly != receipt.PostOnly ||
                placement.ReduceOnly != receipt.ReduceOnly || receipt.SentAtUtc < placement.ReceivedAtUtc ||
                placement.Order.OrderState is "filled" or "cancelled" or "rejected" ||
                receipt.Order.VenueTimestampUtc < placement.Order.VenueTimestampUtc ||
                receipt.Order.FilledNativeAmount < placement.Order.FilledNativeAmount)
                throw new ArgumentException("Cancel lacks an earlier matching placement binding.");
    }
}
