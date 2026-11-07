using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using TradeOps.Application.Oms;

namespace TradeOps.Infrastructure.Persistence.Oms;

/// <summary>Operator-provisioned, bounded FIX store. No implicit session creation, reset, purge,
/// credential persistence or permission provisioning. Every sender transition is atomic.</summary>
public sealed class OmsPostgresFixSessionStoreV1:IOmsFixSessionStoreV1
{
    private readonly NpgsqlDataSource source;
    private readonly OmsFixSessionKeyV1 key;
    private readonly byte[] digest;
    private int disposed;
    private const string Where="tenant_id=@t AND venue_id=@v AND environment=@e AND account_id=@a AND subaccount_id=@s AND sender_comp_id=@sender AND target_comp_id=@target";
    public OmsPostgresFixSessionStoreV1(NpgsqlDataSource source,OmsFixSessionKeyV1 key)
    {key.Validate();this.source=source;this.key=key;digest=OmsRecoveryRunCodec.Encode(key).Sha256;Refresh();}
    private NpgsqlCommand Query(NpgsqlConnection db,NpgsqlTransaction? tx,string sql)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
        var q=new NpgsqlCommand(sql,db,tx);q.Parameters.AddWithValue("t",key.Scope.Tenant.Value);q.Parameters.AddWithValue("v",key.Scope.Venue.Value);
        q.Parameters.AddWithValue("e",(short)key.Scope.Environment);q.Parameters.AddWithValue("a",key.Scope.Account.Value);q.Parameters.AddWithValue("s",key.Scope.Subaccount?.Value??"");
        q.Parameters.AddWithValue("sender",key.SenderCompId);q.Parameters.AddWithValue("target",key.TargetCompId);return q;
    }
    private (DateTime Created,ulong Sender,ulong Target,long Logon) State(NpgsqlConnection db,NpgsqlTransaction? tx,bool locked=false)
    {
        using var q=Query(db,tx,"SELECT schema_version,key_sha256,created_at,next_sender,next_target,last_logon_ms FROM oms_fix_sessions_v1 WHERE "+Where+(locked?" FOR UPDATE":""));
        using var r=q.ExecuteReader();if(!r.Read()||r.GetInt32(0)!=1||!CryptographicOperations.FixedTimeEquals(r.GetFieldValue<byte[]>(1),digest))
            throw new InvalidOperationException("Provisioned FIX session binding required.");
        var sender=r.GetInt64(3);var target=r.GetInt64(4);if(sender<1||target<1)throw new InvalidOperationException("Invalid FIX cursor.");
        return(r.GetDateTime(2),(ulong)sender,(ulong)target,r.GetInt64(5));
    }
    private (DateTime Created,ulong Sender,ulong Target,long Logon) ReadState()
    {using var db=source.OpenConnection();return State(db,null);}
    public DateTime CreationTimeUtc=>ReadState().Created;
    public ulong NextSender=>ReadState().Sender;
    public ulong NextTarget=>ReadState().Target;
    public void Refresh()=>_ = ReadState();
    public void SaveAndAdvance(ulong sequence,string messageType,string? applicationMessage)
    {
        var business=messageType is "D" or "F" or "G";
        if(sequence is <1 or >=1000000000||messageType is not("A" or "0" or "1" or "2" or "3" or "4" or "5" or "D" or "F" or "G")||
            business!=(applicationMessage is not null)||applicationMessage is{} text&&(text.Length>32768||text.Any(c=>c!=1&&(c<32||c>126))||
                text.Split('\u0001').Any(x=>x.StartsWith("553=",StringComparison.Ordinal)||x.StartsWith("554=",StringComparison.Ordinal)||x.StartsWith("96=",StringComparison.Ordinal))))
            throw new ArgumentException("Invalid or sensitive FIX message persistence.");
        using var db=source.OpenConnection();using var tx=db.BeginTransaction();
        using(var durable=new NpgsqlCommand("SET LOCAL synchronous_commit=on",db,tx))durable.ExecuteNonQuery();
        if(State(db,tx,true).Sender!=sequence)throw new InvalidOperationException("FIX sender CAS mismatch.");
        using(var count=Query(db,tx,"SELECT (SELECT count(*) FROM oms_fix_outbound_messages_v1 WHERE "+Where+")+(SELECT count(*) FROM oms_fix_amend_messages_v2 WHERE "+Where+")"))
            if((long)count.ExecuteScalar()!>=10000)throw new InvalidOperationException("FIX journal capacity exceeded; archival requires explicit provisioning.");
        var table=messageType=="G"?"oms_fix_amend_messages_v2":"oms_fix_outbound_messages_v1";
        using(var insert=Query(db,tx,$"""
INSERT INTO {table}(tenant_id,venue_id,environment,account_id,subaccount_id,sender_comp_id,target_comp_id,sequence,schema_version,message_type,application_message,application_sha256)
VALUES(@t,@v,@e,@a,@s,@sender,@target,@seq,@version,@type,@message,@hash)
"""))
        {
            insert.Parameters.AddWithValue("version",messageType=="G"?2:1);insert.Parameters.AddWithValue("seq",(long)sequence);insert.Parameters.AddWithValue("type",messageType);
            insert.Parameters.AddWithValue("message",NpgsqlDbType.Text,(object?)applicationMessage??DBNull.Value);
            insert.Parameters.AddWithValue("hash",NpgsqlDbType.Bytea,applicationMessage is null?DBNull.Value:SHA256.HashData(Encoding.ASCII.GetBytes(applicationMessage)));insert.ExecuteNonQuery();
        }
        using(var update=Query(db,tx,"UPDATE oms_fix_sessions_v1 SET next_sender=next_sender+1 WHERE "+Where))update.ExecuteNonQuery();
        tx.Commit();
    }
    public void AdvanceSender(ulong next)
    {if(next!=NextSender)throw new InvalidOperationException("Use atomic FIX message/cursor persistence.");}
    public void AdvanceTarget(ulong next)
    {
        if(next is <1 or >1000000000)throw new ArgumentOutOfRangeException(nameof(next));
        using var db=source.OpenConnection();using var tx=db.BeginTransaction();var current=State(db,tx,true).Target;
        if(next<current||next>current+10000)throw new InvalidOperationException("FIX target regression or unbounded gap refused.");
        using(var update=Query(db,tx,"UPDATE oms_fix_sessions_v1 SET next_target=@next WHERE "+Where))
        {update.Parameters.AddWithValue("next",(long)next);update.ExecuteNonQuery();}tx.Commit();
    }
    public IReadOnlyList<OmsFixStoredMessageV1> Read(ulong from,ulong through)
    {
        if(from<1||through<from||through-from>=10000||through>=1000000000)throw new ArgumentOutOfRangeException(nameof(through));
        using var db=source.OpenConnection();_ = State(db,null);using var q=Query(db,null,"SELECT * FROM (SELECT sequence,schema_version,message_type,application_message,application_sha256 FROM oms_fix_outbound_messages_v1 WHERE "+Where+" AND sequence BETWEEN @lo AND @hi UNION ALL SELECT sequence,schema_version,message_type,application_message,application_sha256 FROM oms_fix_amend_messages_v2 WHERE "+Where+" AND sequence BETWEEN @lo AND @hi) messages ORDER BY sequence LIMIT 10001");
        q.Parameters.AddWithValue("lo",(long)from);q.Parameters.AddWithValue("hi",(long)through);using var rows=q.ExecuteReader();var result=new List<OmsFixStoredMessageV1>();
        while(rows.Read())
        {
            if(result.Count>=10000||rows.GetInt32(1)!=(rows.GetString(2)=="G"?2:1))throw new InvalidOperationException("FIX store schema/capacity mismatch.");
            var text=rows.IsDBNull(3)?null:rows.GetString(3);
            if(text is not null&&!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.ASCII.GetBytes(text)),rows.GetFieldValue<byte[]>(4)))throw new InvalidOperationException("FIX message digest mismatch.");
            result.Add(new((ulong)rows.GetInt64(0),rows.GetString(2),text));
        }
        return result.AsReadOnly();
    }
    public long ReserveLogonTimestamp(long utcMilliseconds)
    {
        if(utcMilliseconds<=0)throw new ArgumentOutOfRangeException(nameof(utcMilliseconds));
        using var db=source.OpenConnection();using var tx=db.BeginTransaction();var state=State(db,tx,true);var next=Math.Max(utcMilliseconds,checked(state.Logon+1));
        if(next-utcMilliseconds>2000)throw new InvalidOperationException("FIX authentication clock regressed.");
        using(var update=Query(db,tx,"UPDATE oms_fix_sessions_v1 SET last_logon_ms=@next WHERE "+Where))
        {update.Parameters.AddWithValue("next",next);update.ExecuteNonQuery();}tx.Commit();return next;
    }
    public void Dispose()=>Interlocked.Exchange(ref disposed,1);
}
