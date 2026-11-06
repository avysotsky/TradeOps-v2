using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

/// <summary>A stable protocol identity, distinct from a connection epoch or an OMS command ID.</summary>
public sealed record OmsFixSessionKeyV1(int SchemaVersion,AccountKey Scope,string SenderCompId,string TargetCompId)
{
    public void Validate()
    {
        if(SchemaVersion!=1||Scope.Venue.Value!="DERIBIT"||Scope.Subaccount is not null||
            Scope.Environment is not(OmsEnvironment.Mainnet or OmsEnvironment.Testnet)||TargetCompId!="DERIBITSERVER"||
            string.IsNullOrWhiteSpace(SenderCompId)||SenderCompId.Length>64||SenderCompId.Any(c=>c<33||c>126||c=='='))
            throw new ArgumentException("Invalid pinned FIX session identity.");
    }
}

public sealed record OmsFixStoredMessageV1(ulong Sequence,string MessageType,string? ApplicationMessage);
/// <summary>Synchronous durable port required by the engine. SaveAndAdvance is atomic and flushes
/// before physical send. Sequence reset never erases the audit or silently starts a new generation.</summary>
public interface IOmsFixSessionStoreV1:IDisposable
{
    DateTime CreationTimeUtc {get;}
    ulong NextSender {get;}
    ulong NextTarget {get;}
    void SaveAndAdvance(ulong sequence,string messageType,string? applicationMessage);
    void AdvanceSender(ulong next);
    void AdvanceTarget(ulong next);
    IReadOnlyList<OmsFixStoredMessageV1> Read(ulong from,ulong through);
    long ReserveLogonTimestamp(long utcMilliseconds);
    void Refresh();
}

/// <summary>Owned process capability for every FIX physical write, including engine admin/replay.
/// Only Infrastructure can issue it; it cannot be restored from configuration or serialized state.</summary>
public sealed class OmsPhysicalSessionAuthority
{
    private readonly Func<Func<Task>,Task> initiate;
    internal OmsPhysicalSessionAuthority(Func<Func<Task>,Task> initiate)=>this.initiate=initiate;
    public Task InitiateAsync(Func<Task> physicalWrite)=>initiate(physicalWrite);
}
