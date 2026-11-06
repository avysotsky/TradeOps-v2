using Npgsql;
using NpgsqlTypes;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;

namespace TradeOps.Infrastructure.Persistence.Oms;

public static class OmsPostgresFixParentReaderV1
{
    public static async Task<DeribitNativeCommandView?> ReadAsync(NpgsqlDataSource source,OmsJournalCommandRequest cancel,CancellationToken ct)
    {
        await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);
        var parent=await OmsPostgresRiskAdmission.ReadCancelParentAsync(db,tx,cancel,ct);await tx.CommitAsync(ct);return parent;
    }
}

/// <summary>Binding and native report reads share the caller's B4 repeatable-read transaction.
/// No transport, migrations, grants or journal repair.</summary>
internal static class OmsPostgresFixCommandEvidenceV1
{
    private const string Where="tenant_id=@t AND venue_id=@v AND environment=@e AND account_id=@a AND subaccount_id=@s";
    private static NpgsqlCommand Query(NpgsqlConnection db,NpgsqlTransaction tx,AccountKey scope,string sql)
    {
        var q=new NpgsqlCommand(sql,db,tx);q.Parameters.AddWithValue("t",scope.Tenant.Value);q.Parameters.AddWithValue("v",scope.Venue.Value);
        q.Parameters.AddWithValue("e",(short)scope.Environment);q.Parameters.AddWithValue("a",scope.Account.Value);q.Parameters.AddWithValue("s",scope.Subaccount?.Value??"");return q;
    }
    internal static async Task InsertAsync(NpgsqlConnection db,NpgsqlTransaction tx,DeribitFixCommandBindingV1 binding,CancellationToken ct)
    {
        binding.Validate();var encoded=OmsRecoveryRunCodec.Encode(binding);
        await using(var count=Query(db,tx,binding.Command.Scope,"SELECT count(*) FROM oms_deribit_fix_command_bindings_v1 WHERE "+Where))
            if((long)(await count.ExecuteScalarAsync(ct))!>=10000)throw new InvalidOperationException("FIX binding capacity exceeded.");
        await using var q=Query(db,tx,binding.Command.Scope,"""
INSERT INTO oms_deribit_fix_command_bindings_v1(tenant_id,venue_id,environment,account_id,subaccount_id,command_id,schema_version,binding_json,binding_sha256,command_sha256)
VALUES(@t,@v,@e,@a,@s,@command,1,@json,@sha,@commandSha)
""");
        q.Parameters.AddWithValue("command",binding.Command.CommandId.Value);q.Parameters.AddWithValue("json",NpgsqlDbType.Jsonb,encoded.Json);
        q.Parameters.AddWithValue("sha",encoded.Sha256);q.Parameters.AddWithValue("commandSha",OmsJournalCanonical.EncodeCommand(binding.Command).Sha256);
        await q.ExecuteNonQueryAsync(ct);
    }

    internal static async Task<(List<DeribitFixCommandBindingV1> Bindings,List<DeribitFixReportEnvelopeV1> Reports)> ReadAsync(
        NpgsqlConnection db,NpgsqlTransaction tx,AccountKey scope,int maximum,DateTimeOffset readAt,CancellationToken ct)
    {
        var bindings=new List<DeribitFixCommandBindingV1>();var reports=new List<DeribitFixReportEnvelopeV1>();var bytes=0;
        var reportIds=new Dictionary<Guid,DeribitFixReportEnvelopeV1>();
        await using(var q=Query(db,tx,scope,"SELECT command_id,b.schema_version,binding_json::text,binding_sha256,command_sha256,b.recorded_at,c.payload_sha256,c.request_json::text FROM oms_deribit_fix_command_bindings_v1 b JOIN oms_commands c USING(tenant_id,venue_id,environment,account_id,subaccount_id,command_id) WHERE "+"b.tenant_id=@t AND b.venue_id=@v AND b.environment=@e AND b.account_id=@a AND b.subaccount_id=@s"+" ORDER BY command_id LIMIT @limit"))
        {
            q.Parameters.AddWithValue("limit",maximum+1);await using var rows=await q.ExecuteReaderAsync(ct);
            while(await rows.ReadAsync(ct))
            {
                if(bindings.Count==maximum)throw new ArgumentOutOfRangeException(nameof(maximum));
                var raw=rows.GetString(2);bytes=checked(bytes+System.Text.Encoding.UTF8.GetByteCount(raw));if(bytes>16*1024*1024)throw new ArgumentOutOfRangeException(nameof(maximum));
                var binding=OmsRecoveryRunCodec.Decode<DeribitFixCommandBindingV1>(raw,rows.GetFieldValue<byte[]>(3));binding.Validate();
                if(rows.GetInt32(1)!=1||binding.Command.Scope!=scope||binding.Command.CommandId.Value!=rows.GetGuid(0)||
                    !OmsJournalCanonical.EncodeCommand(binding.Command).Sha256.SequenceEqual(rows.GetFieldValue<byte[]>(4))||
                    !rows.GetFieldValue<byte[]>(4).SequenceEqual(rows.GetFieldValue<byte[]>(6))||
                    OmsJournalRecoveryCommandCodec.Decode(rows.GetString(7),scope)!=binding.Command||rows.GetDateTime(5)>readAt.UtcDateTime)
                    throw new ArgumentException("Corrupt FIX command binding.");
                bindings.Add(binding);
            }
        }
        await using(var q=Query(db,tx,scope,"SELECT schema_version,report_json::text,report_sha256,semantic_sha256,event_key,sender_comp_id,target_comp_id,connection_epoch,sequence,message_type,recorded_at,report_id FROM oms_deribit_fix_reports_v1 WHERE "+Where+" ORDER BY recorded_at,report_id LIMIT @limit"))
        {
            q.Parameters.AddWithValue("limit",maximum+1);await using var rows=await q.ExecuteReaderAsync(ct);
            while(await rows.ReadAsync(ct))
            {
                if(reports.Count==maximum)throw new ArgumentOutOfRangeException(nameof(maximum));
                var raw=rows.GetString(1);bytes=checked(bytes+System.Text.Encoding.UTF8.GetByteCount(raw));if(bytes>16*1024*1024)throw new ArgumentOutOfRangeException(nameof(maximum));
                var report=OmsRecoveryRunCodec.Decode<DeribitFixReportEnvelopeV1>(raw,rows.GetFieldValue<byte[]>(2));report.Validate();
                var key=report.GetSession();
                if(rows.GetInt32(0)!=1||key.Scope!=scope||key.SenderCompId!=rows.GetString(5)||key.TargetCompId!=rows.GetString(6)||
                    report.GetEpoch()!=rows.GetGuid(7)||report.GetSequence()!=(ulong)rows.GetInt64(8)||report.GetMessageType()!=rows.GetString(9)||
                    !Convert.FromHexString(report.GetSemanticSha256()).SequenceEqual(rows.GetFieldValue<byte[]>(3))||
                    !report.GetNativeEventKey().SequenceEqual(rows.GetFieldValue<byte[]>(4))||rows.GetDateTime(10)>readAt.UtcDateTime)
                    throw new ArgumentException("Corrupt FIX native evidence.");
                reports.Add(report);
                reportIds.Add(rows.GetGuid(11),report);
            }
        }
        var events=new HashSet<string>(StringComparer.Ordinal);
        var eventDigests=new Dictionary<string,string>(StringComparer.Ordinal);
        await using(var q=Query(db,tx,scope,"SELECT schema_version,event_key,semantic_sha256,first_report_id FROM oms_deribit_fix_native_events_v1 WHERE "+Where+" LIMIT @limit"))
        {
            q.Parameters.AddWithValue("limit",maximum+1);await using var rows=await q.ExecuteReaderAsync(ct);
            while(await rows.ReadAsync(ct))
            {
                if(events.Count==maximum)throw new ArgumentOutOfRangeException(nameof(maximum));
                if(rows.GetInt32(0)!=1||!reportIds.TryGetValue(rows.GetGuid(3),out var first)||
                    !first.GetNativeEventKey().SequenceEqual(rows.GetFieldValue<byte[]>(1))||
                    !Convert.FromHexString(first.GetSemanticSha256()).SequenceEqual(rows.GetFieldValue<byte[]>(2))||
                    !events.Add(Convert.ToHexString(rows.GetFieldValue<byte[]>(1))))throw new ArgumentException("Corrupt native FIX event index.");
                eventDigests.Add(Convert.ToHexString(rows.GetFieldValue<byte[]>(1)),Convert.ToHexString(rows.GetFieldValue<byte[]>(2)));
            }
        }
        if(!events.SetEquals(reports.Select(x=>Convert.ToHexString(x.GetNativeEventKey()))))throw new ArgumentException("Incomplete native FIX event index.");
        if(reports.Any(x=>eventDigests[Convert.ToHexString(x.GetNativeEventKey())]!=x.GetSemanticSha256()))throw new ArgumentException("Native FIX semantic collision.");
        await using(var q=Query(db,tx,scope,"SELECT EXISTS(SELECT 1 FROM oms_deribit_fix_report_incidents_v1 WHERE "+Where+")"))
            if(await q.ExecuteScalarAsync(ct) is true)throw new ArgumentException("Native FIX evidence collision requires review.");
        return(bindings,reports);
    }

    internal static async Task<IReadOnlyList<DeribitNativeCommandView>> ViewsAsync(NpgsqlConnection db,NpgsqlTransaction tx,AccountKey scope,CancellationToken ct)
    {
        var now=DateTimeOffset.UtcNow;var (bindings,reports)=await ReadAsync(db,tx,scope,10000,now,ct);
        var views=new List<DeribitNativeCommandView>();
        foreach(var binding in bindings)
        {
            var matches=reports.Where(x=>x.Execution is not null).Select(x=>binding.Match(x.Execution!)).Where(x=>x is not null)
                .OrderBy(x=>x!.Order.VenueTimestampUtc).ThenBy(x=>x!.ReceivedAtUtc).ToArray();
            if(matches.Length>0)views.Add(matches[0]!);
        }
        return views;
    }
}
