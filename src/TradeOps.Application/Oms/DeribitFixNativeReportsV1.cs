using TradeOps.Domain.Enums;

namespace TradeOps.Application.Oms;

/// <summary>Native FIX evidence. Server tag 11, client tag 41, order tag 37 and execution/change
/// identifiers are distinct. This is never a JSON-RPC receipt or an additional portfolio fill.</summary>
public sealed record DeribitFixExecutionReportV1(int SchemaVersion,OmsFixSessionKeyV1 Session,Guid ConnectionEpoch,
    ulong Sequence,bool PossDup,DateTimeOffset SendingTimeUtc,DateTimeOffset ReceivedAtUtc,DateTimeOffset TransactTimeUtc,
    string? NativeOrderId,string? ServerAssignedClOrdId,string? OriginalClientClOrdId,string? ExecId,string? ChangeId,
    string Instrument,OrderSide Side,string OrderStatus,string? ExecType,decimal OrderQty,decimal? CashOrderQty,
    decimal CumQty,decimal LeavesQty,int? QtyType,decimal? ContractMultiplier,string OrderType,decimal? Price,
    string? TimeInForce,string ExecutionInstructions,string? Label,int RejectReason,decimal? LastQty,decimal? LastPrice,string? TradeMatchId)
{
    public void Validate()
    {
        Session.Validate();
        if(SchemaVersion!=1||ConnectionEpoch==Guid.Empty||Sequence is <1 or >1000000000||!Enum.IsDefined(Side)||
            SendingTimeUtc.Offset!=TimeSpan.Zero||ReceivedAtUtc.Offset!=TimeSpan.Zero||TransactTimeUtc.Offset!=TimeSpan.Zero||
            SendingTimeUtc>ReceivedAtUtc.AddSeconds(5)||TransactTimeUtc>SendingTimeUtc.AddSeconds(5)||
            OrderStatus is not("0" or "1" or "2" or "4" or "8")||OrderQty<0||CumQty<0||LeavesQty<0||CashOrderQty<0||
            CashOrderQty is{} cash&&cash!=OrderQty||ContractMultiplier<=0||Price<0||LastQty<0||LastPrice<0||
            QtyType is not(null or 0 or 1)||RejectReason<0||OrderType.Length!=1||ExecutionInstructions.Length>16||
            ExecutionInstructions.Any(c=>c is not('6' or 'A' or 'E' or ' '))||ExecutionInstructions.Contains('A')&&!ExecutionInstructions.Contains('6')||
            ExecType=="F"&&(TradeMatchId is null||LastQty is null or <=0||LastPrice is null or <=0)||
            new[]{NativeOrderId,ServerAssignedClOrdId,OriginalClientClOrdId,ExecId,ChangeId,Label,TradeMatchId,ExecType,TimeInForce}
                .Any(x=>x is not null&&(x.Length is <1 or >160||x.Any(c=>c<32||c>126)))||
            Instrument.Length is <1 or >160||Instrument.Any(c=>c<33||c>126))throw new ArgumentException("Invalid native Deribit FIX execution report.");
    }
    /// <summary>Current documentation mixes USD value-order and contract quantity descriptions.
    /// Only a separately verified one-USD inverse contract makes every quantity unambiguous.
    /// BTC multiplier 10 and other ambiguous products remain native evidence, never approval.</summary>
    public bool HasUnambiguousUsdAmounts(decimal verifiedNativeContractUsd)
    {
        Validate();
        return verifiedNativeContractUsd==1&&ContractMultiplier is null or 1&&CashOrderQty==OrderQty&&OrderQty>0&&
            CumQty<=OrderQty&&LeavesQty<=OrderQty-CumQty&&
            (OrderStatus!="2"||CumQty==OrderQty&&LeavesQty==0)&&
            (OrderStatus!="0"||CumQty==0)&&(OrderStatus!="1"||CumQty>0&&CumQty<OrderQty)&&
            (LastQty is null||LastQty<=CumQty);
    }
    public string SemanticSha256()=>Convert.ToHexString(OmsRecoveryRunCodec.Encode(new{
        // Tag 17 has no qualified cross-connection namespace in the Deribit production profile.
        // Preserve it in the raw contract, but do not assume that it is the native trade ID.
        SchemaVersion,Session.Scope,NativeOrderId,ServerAssignedClOrdId,OriginalClientClOrdId,ChangeId,Instrument,Side,OrderStatus,ExecType,
        OrderQty,CashOrderQty,CumQty,LeavesQty,QtyType,ContractMultiplier,OrderType,Price,TimeInForce,ExecutionInstructions,Label,RejectReason,LastQty,LastPrice,TradeMatchId,TransactTimeUtc
    }).Sha256);
}

public sealed record DeribitFixCancelRejectV1(int SchemaVersion,OmsFixSessionKeyV1 Session,Guid ConnectionEpoch,ulong Sequence,
    bool PossDup,DateTimeOffset SendingTimeUtc,DateTimeOffset ReceivedAtUtc,string? NativeOrderId,string? ServerAssignedClOrdId,
    string? OriginalClOrdId,string OrderStatus,int RejectReason,string ResponseTo,string? Instrument,string? Label)
{
    public void Validate()
    {
        Session.Validate();
        if(SchemaVersion!=1||ConnectionEpoch==Guid.Empty||Sequence is <1 or >1000000000||
            SendingTimeUtc.Offset!=TimeSpan.Zero||ReceivedAtUtc.Offset!=TimeSpan.Zero||SendingTimeUtc>ReceivedAtUtc.AddSeconds(5)||
            RejectReason<0||ResponseTo is not("1" or "2")||OrderStatus is not("0" or "1" or "2" or "4" or "8")||
            new[]{NativeOrderId,ServerAssignedClOrdId,OriginalClOrdId,Instrument,Label}.Any(x=>x is not null&&(x.Length is <1 or >160||x.Any(c=>c<32||c>126))))
            throw new ArgumentException("Invalid native FIX cancel rejection.");
    }
}
