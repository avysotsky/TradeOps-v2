using System.Buffers.Binary;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;

namespace TradeOps.Infrastructure.Persistence.Oms;

/// <summary>Persistent budget comes from immutable B5 commands, never a resettable file counter.
/// Advisory arbitration is additional to the mandatory OS physical owner, not a substitute.</summary>
public sealed class OmsPostgresFixQualificationLeaseV1:IAsyncDisposable,IOmsFixQualificationLeaseV1
{
    private readonly NpgsqlConnection connection;private readonly NpgsqlDataSource source;private readonly AccountKey scope;
    private readonly int backend;private readonly long key;
    private OmsPostgresFixQualificationLeaseV1(NpgsqlConnection connection,NpgsqlDataSource source,AccountKey scope,long key)
    {this.connection=connection;this.source=source;this.scope=scope;this.key=key;backend=connection.ProcessID;}
    public static async Task<OmsPostgresFixQualificationLeaseV1> AcquireAsync(NpgsqlDataSource source,AccountKey scope,CancellationToken ct)
    {
        if(scope.Venue.Value!="DERIBIT"||scope.Environment!=OmsEnvironment.Mainnet||scope.Subaccount is not null)throw new ArgumentException("Pinned main-account mainnet scope required.");
        var key=BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes("owner-fix-eth-qualification-v1:"+OmsRecoveryRunCodec.Encode(scope).Json)));
        var c=await source.OpenConnectionAsync(ct);
        try
        {
            await using var q=new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)",c);q.Parameters.AddWithValue("key",key);
            if(await q.ExecuteScalarAsync(ct) is not true)throw new InvalidOperationException("Qualification scope already owned.");
            return new(c,source,scope,key);
        }
        catch{await c.DisposeAsync();throw;}
    }
    public async Task CheckAsync(CancellationToken ct)
    {
        if(connection.State!=ConnectionState.Open)throw new InvalidOperationException("Qualification lease lost.");
        await using var q=new NpgsqlCommand("SELECT pg_backend_pid()",connection);
        if(await q.ExecuteScalarAsync(ct) is not int actual||actual!=backend)throw new InvalidOperationException("Qualification DB session changed.");
    }
    public async Task<int> CheckBudgetAsync(OmsRecoveredAccountStateV1 current,CancellationToken ct)
    {
        await CheckAsync(ct);if(current.Scope!=scope||DateTimeOffset.UtcNow>current.UsableThroughUtc)throw new InvalidOperationException("Fresh scoped recovery required.");
        var snapshot=await new OmsPostgresRecoveryLedgerReader(source,true).ReadAsync(scope,cancellationToken:ct);
        if(!snapshot.IsUsable)throw new InvalidOperationException("Qualification journal unavailable.");
        var commands=snapshot.Commands.Where(x=>x.Request.Payload.Provenance==DeribitFixQualificationScenarioV1.Provenance).ToArray();
        var placements=commands.Where(x=>x.Request.Payload.Kind==OmsJournalCommandKind.Place).ToArray();
        if(placements.Length>3||commands.Any(x=>x.Request.Payload.Instrument!="ETH-PERPETUAL"||x.Request.Payload.Quantity!=1||
            !x.Request.Payload.PostOnly||x.Request.Payload.ReduceOnly||x.Request.Payload.Side!=TradeOps.Domain.Enums.OrderSide.Buy||
            x.Request.Payload.OperatorOrStrategyId!="owner-fix-qualification"||x.Phase!=OmsRecoveryDispatchPhase.Acknowledged))
            throw new InvalidOperationException("Prior qualification attempt needs review; no automatic retry.");
        foreach(var placement in placements)
        {
            var order=current.Orders.SingleOrDefault(x=>x.LocalOrderId==placement.Request.LocalOrderId);
            if(order is null||order.State.NativeState!="cancelled"||order.State.KnownFilledNativeAmount!=0||
                current.Executions.Any(x=>x.Currency==order.State.Currency&&x.ExchangeOrderId==order.State.ExchangeOrderId))
                throw new InvalidOperationException("Prior qualification is not terminal and unfilled.");
        }
        return placements.Length;
    }
    public async ValueTask DisposeAsync()
    {
        try{if(connection.State==ConnectionState.Open){await using var q=new NpgsqlCommand("SELECT pg_advisory_unlock(@key)",connection);q.Parameters.AddWithValue("key",key);await q.ExecuteScalarAsync();}}
        finally{await connection.DisposeAsync();}
    }
}
