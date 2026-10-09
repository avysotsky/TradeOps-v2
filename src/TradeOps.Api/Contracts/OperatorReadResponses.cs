using TradeOps.Application.Models;
using TradeOps.Domain.Enums;

namespace TradeOps.Api.Contracts;

public sealed record TradingSignalAuditResponse(
    Guid SignalId,
    string Symbol,
    OrderSide Side,
    string? SignalType,
    decimal RequestedQuantity,
    decimal? RiskPercent,
    decimal? StopLoss,
    decimal? TakeProfit,
    DateTimeOffset ReceivedAt,
    string? Source,
    SignalOutcome Outcome,
    IReadOnlyCollection<string> RiskRejectionReasons,
    Guid? OrderId,
    string? ClientOrderId,
    string? ExecutionIssueCode,
    string? ExecutionIssueMessage,
    DateTimeOffset? ExecutionIssueAt);

public sealed record LocalOrderResponse(
    Guid Id,
    string? ExchangeOrderId,
    string ClientOrderId,
    string Symbol,
    OrderSide Side,
    OrderType OrderType,
    decimal RequestedQuantity,
    decimal FilledQuantity,
    decimal? AverageFillPrice,
    decimal? Price,
    OrderStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record OrderLifecycleEventResponse(
    Guid Id,
    Guid OrderId,
    string ClientOrderId,
    OrderStatus? PreviousStatus,
    OrderStatus Status,
    decimal FilledQuantity,
    decimal? AverageFillPrice,
    string? ExchangeOrderId,
    string Source,
    DateTimeOffset OccurredAt);

public sealed record OperationalRunStatusResponse(
    string RunType,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    bool IsRunning,
    bool? Succeeded,
    int OrdersScanned,
    int OrdersUpdated,
    int OrderIssues,
    int OrdersMissingOnExchange,
    int PositionsCompared,
    int PositionMismatches,
    int PositionSnapshots,
    string? ErrorMessage,
    DateTimeOffset UpdatedAt);

public sealed record OperationalRunResponse(
    Guid Id,
    string RunType,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    bool IsRunning,
    bool? Succeeded,
    int OrdersScanned,
    int OrdersUpdated,
    int OrderIssues,
    int OrdersMissingOnExchange,
    int PositionsCompared,
    int PositionMismatches,
    int PositionSnapshots,
    string? ErrorMessage,
    DateTimeOffset UpdatedAt);

public sealed record OrderCancellationResponse(
    OrderCancellationOutcome Outcome,
    LocalOrderResponse? Order,
    string? Message);

public sealed record BulkOrderCancellationResponse(
    string? Symbol,
    int CandidateCount,
    int CancelledCount,
    int AlreadyCancelledCount,
    int CancellationRequestedCount,
    int NotCancellableCount,
    int NotFoundCount,
    int UnresolvedCount,
    bool IsComplete,
    IReadOnlyCollection<OrderCancellationResponse> Results);

public sealed record EmergencyStopResponse(
    bool TradingEnabled,
    bool EmergencyStop,
    string? EmergencyStopReason,
    string SettlementCurrency,
    decimal DailyGrossRealizedPnL,
    decimal DailySettlementFees,
    decimal? DailyNetRealizedPnL,
    IReadOnlyCollection<UnconvertedFee> UnconvertedFees,
    bool IsDailyAccountingComplete,
    int ActivePositionMismatchCount,
    bool HasPositionMismatch,
    DateTimeOffset UpdatedAt,
    BulkOrderCancellationResponse? OrderCancellation);

public sealed record FillAuditResponse(
    Guid FillId,
    Guid OrderId,
    string ExchangeFillId,
    string ClientOrderId,
    string Symbol,
    OrderSide Side,
    decimal Quantity,
    decimal Price,
    decimal? Fee,
    string? FeeCurrency,
    DateTimeOffset FilledAt);

public sealed record UnconvertedFeeResponse(
    string ExchangeFillId,
    decimal Amount,
    string? Currency,
    DateTimeOffset FilledAt);

public sealed record DailyPnlResponse(
    DateOnly UtcDate,
    string SettlementCurrency,
    decimal GrossRealizedPnL,
    decimal SettlementFees,
    decimal? NetRealizedPnL,
    IReadOnlyCollection<UnconvertedFeeResponse> UnconvertedFees,
    bool IsComplete);
