using System.Security.Cryptography;
using System.Text;

namespace TradeOps.Application.Oms;

public enum DeribitFixReportWriteResult { Inserted=1,Duplicate,Collision,ObservedNativeDuplicate }
public interface IDeribitFixNativeReportJournal
{
    DeribitFixReportWriteResult Record(DeribitFixReportEnvelopeV1 report);
}
/// <summary>New durable native evidence contract, never an extension of receipt v1 encoding.</summary>
public sealed record DeribitFixReportEnvelopeV1(int SchemaVersion,DeribitFixExecutionReportV1? Execution,
    DeribitFixCancelRejectV1? CancelReject)
{
    public void Validate()
    {if(SchemaVersion!=1||(Execution is null)==(CancelReject is null))throw new ArgumentException("Exactly one native FIX report is required.");Execution?.Validate();CancelReject?.Validate();}
    public OmsFixSessionKeyV1 GetSession()=>Execution?.Session??CancelReject!.Session;
    public Guid GetEpoch()=>Execution?.ConnectionEpoch??CancelReject!.ConnectionEpoch;
    public ulong GetSequence()=>Execution?.Sequence??CancelReject!.Sequence;
    public string GetMessageType()=>Execution is not null?"8":"9";
    public string GetSemanticSha256()=>Execution?.SemanticSha256()??Convert.ToHexString(OmsRecoveryRunCodec.Encode(new{
        CancelReject!.SchemaVersion,CancelReject.Session.Scope,CancelReject.NativeOrderId,CancelReject.ServerAssignedClOrdId,
        CancelReject.OriginalClOrdId,CancelReject.OrderStatus,CancelReject.RejectReason,CancelReject.ResponseTo,CancelReject.Instrument,CancelReject.Label
    }).Sha256);
    public byte[] GetNativeEventKey()
    {
        var native=Execution is{ExecType:"F",TradeMatchId:not null} e?"trade:"+e.Instrument+":"+e.TradeMatchId:
            Execution is{ChangeId:not null} change?"order-change:"+change.Instrument+":"+change.ChangeId:"body:"+GetSemanticSha256();
        return SHA256.HashData(Encoding.UTF8.GetBytes("deribit-fix-native-event-v1:"+native));
    }
}
