using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using TradeOps.Application.Oms;

namespace TradeOps.Infrastructure.Persistence.Oms;

/// <summary>Native FIX evidence/semantic dedup only. No WS receipt fabrication, portfolio updates,
/// resend, reset or send authority. Caller retains before advancing its protocol target cursor.</summary>
public sealed class OmsPostgresDeribitFixReportJournalV1(NpgsqlDataSource source,OmsFixSessionKeyV1 session):IDeribitFixNativeReportJournal
{
    private const string AccountWhere="tenant_id=@t AND venue_id=@v AND environment=@e AND account_id=@a AND subaccount_id=@s";
    private const string SessionWhere=AccountWhere+" AND sender_comp_id=@sender AND target_comp_id=@target";
    private NpgsqlCommand Query(NpgsqlConnection db,NpgsqlTransaction tx,string sql)
    {
        var q=new NpgsqlCommand(sql,db,tx);q.Parameters.AddWithValue("t",session.Scope.Tenant.Value);q.Parameters.AddWithValue("v",session.Scope.Venue.Value);
        q.Parameters.AddWithValue("e",(short)session.Scope.Environment);q.Parameters.AddWithValue("a",session.Scope.Account.Value);q.Parameters.AddWithValue("s",session.Scope.Subaccount?.Value??"");
        q.Parameters.AddWithValue("sender",session.SenderCompId);q.Parameters.AddWithValue("target",session.TargetCompId);return q;
    }
    public DeribitFixReportWriteResult Record(DeribitFixReportEnvelopeV1 input)
    {
        session.Validate();input.Validate();if(input.GetSession()!=session)throw new ArgumentException("Native report belongs to another session/scope.");
        var encoded=OmsRecoveryRunCodec.Encode(input);if(Encoding.UTF8.GetByteCount(encoded.Json)>65536)throw new ArgumentException("Native FIX report capacity exceeded.");
        var semantic=Convert.FromHexString(input.GetSemanticSha256());var eventKey=input.GetNativeEventKey();
        using var db=source.OpenConnection();using var tx=db.BeginTransaction();
        // Account-scoped journal arbitration, not a transport/owner lease.
        using(var locked=Query(db,tx,"SELECT pg_advisory_xact_lock(@lock)"))
        {locked.Parameters.AddWithValue("lock",BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes("fix-native-report-v1:"+OmsRecoveryRunCodec.Encode(session.Scope).Json))));locked.ExecuteNonQuery();}
        byte[]? priorSemantic=null;
        using(var q=Query(db,tx,"SELECT schema_version,CASE WHEN octet_length(report_json::text)<=65536 THEN report_json::text END,report_sha256,semantic_sha256,event_key,message_type FROM oms_deribit_fix_reports_v1 WHERE "+SessionWhere+" AND connection_epoch=@epoch AND sequence=@sequence"))
        {
            q.Parameters.AddWithValue("epoch",input.GetEpoch());q.Parameters.AddWithValue("sequence",(long)input.GetSequence());using var r=q.ExecuteReader();
            if(r.Read())
            {
                if(r.GetInt32(0)!=1||r.IsDBNull(1))throw new InvalidOperationException("Native FIX journal schema/capacity mismatch.");
                var prior=OmsRecoveryRunCodec.Decode<DeribitFixReportEnvelopeV1>(r.GetString(1),r.GetFieldValue<byte[]>(2));prior.Validate();
                priorSemantic=r.GetFieldValue<byte[]>(3);
                if(prior.GetSession()!=session||prior.GetEpoch()!=input.GetEpoch()||prior.GetSequence()!=input.GetSequence()||
                    !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(prior.GetSemanticSha256()),priorSemantic)||
                    !CryptographicOperations.FixedTimeEquals(prior.GetNativeEventKey(),r.GetFieldValue<byte[]>(4))||prior.GetMessageType()!=r.GetString(5))
                    throw new InvalidOperationException("Corrupt native FIX observation binding.");
            }
        }
        if(priorSemantic is not null)
        {
            if(CryptographicOperations.FixedTimeEquals(priorSemantic,semantic)){tx.Commit();return DeribitFixReportWriteResult.Duplicate;}
            Incident("ObservationCollision");tx.Commit();return DeribitFixReportWriteResult.Collision;
        }
        bool nativeDuplicate;
        using(var q=Query(db,tx,"""
SELECT e.semantic_sha256,e.schema_version,r.schema_version,
 CASE WHEN octet_length(r.report_json::text)<=65536 THEN r.report_json::text END,r.report_sha256,r.semantic_sha256,r.event_key,
 r.connection_epoch,r.sequence,r.sender_comp_id,r.target_comp_id,r.message_type
FROM oms_deribit_fix_native_events_v1 e JOIN oms_deribit_fix_reports_v1 r
 ON (r.tenant_id,r.venue_id,r.environment,r.account_id,r.subaccount_id,r.report_id)=
 (e.tenant_id,e.venue_id,e.environment,e.account_id,e.subaccount_id,e.first_report_id)
WHERE e.tenant_id=@t AND e.venue_id=@v AND e.environment=@e AND e.account_id=@a AND e.subaccount_id=@s AND e.event_key=@event
"""))
        {
            q.Parameters.AddWithValue("event",eventKey);using var r=q.ExecuteReader();nativeDuplicate=r.Read();
            if(nativeDuplicate)
            {
                if(r.GetInt32(1)!=1||r.GetInt32(2)!=1||r.IsDBNull(3))throw new InvalidOperationException("Unsupported native FIX event schema.");
                priorSemantic=r.GetFieldValue<byte[]>(0);
                var first=OmsRecoveryRunCodec.Decode<DeribitFixReportEnvelopeV1>(r.GetString(3),r.GetFieldValue<byte[]>(4));first.Validate();
                if(first.GetSession().Scope!=session.Scope||!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(first.GetSemanticSha256()),priorSemantic)||
                    !CryptographicOperations.FixedTimeEquals(priorSemantic,r.GetFieldValue<byte[]>(5))||
                    !CryptographicOperations.FixedTimeEquals(first.GetNativeEventKey(),eventKey)||!CryptographicOperations.FixedTimeEquals(eventKey,r.GetFieldValue<byte[]>(6))||
                    first.GetEpoch()!=r.GetGuid(7)||first.GetSequence()!=(ulong)r.GetInt64(8)||first.GetSession().SenderCompId!=r.GetString(9)||
                    first.GetSession().TargetCompId!=r.GetString(10)||first.GetMessageType()!=r.GetString(11))
                    throw new InvalidOperationException("Corrupt native FIX semantic event binding.");
            }
        }
        if(nativeDuplicate&&!CryptographicOperations.FixedTimeEquals(priorSemantic!,semantic))
        {Incident("NativeEventCollision");tx.Commit();return DeribitFixReportWriteResult.Collision;}
        EnsureCapacity();var reportId=Guid.NewGuid();
        using(var q=Query(db,tx,"""
INSERT INTO oms_deribit_fix_reports_v1(tenant_id,venue_id,environment,account_id,subaccount_id,sender_comp_id,target_comp_id,connection_epoch,sequence,
 report_id,schema_version,message_type,report_json,report_sha256,semantic_sha256,event_key)
VALUES(@t,@v,@e,@a,@s,@sender,@target,@epoch,@sequence,@id,1,@type,@json,@sha,@semantic,@event)
"""))
        {
            q.Parameters.AddWithValue("epoch",input.GetEpoch());q.Parameters.AddWithValue("sequence",(long)input.GetSequence());q.Parameters.AddWithValue("id",reportId);
            q.Parameters.AddWithValue("type",input.GetMessageType());q.Parameters.AddWithValue("json",NpgsqlDbType.Jsonb,encoded.Json);q.Parameters.AddWithValue("sha",encoded.Sha256);
            q.Parameters.AddWithValue("semantic",semantic);q.Parameters.AddWithValue("event",eventKey);q.ExecuteNonQuery();
        }
        if(!nativeDuplicate)
        {
            using var q=Query(db,tx,"""
INSERT INTO oms_deribit_fix_native_events_v1(tenant_id,venue_id,environment,account_id,subaccount_id,event_key,semantic_sha256,first_report_id,schema_version)
VALUES(@t,@v,@e,@a,@s,@event,@semantic,@id,1)
""");q.Parameters.AddWithValue("event",eventKey);q.Parameters.AddWithValue("semantic",semantic);q.Parameters.AddWithValue("id",reportId);q.ExecuteNonQuery();
        }
        tx.Commit();return nativeDuplicate?DeribitFixReportWriteResult.ObservedNativeDuplicate:DeribitFixReportWriteResult.Inserted;

        void Incident(string reason)
        {
            EnsureCapacity();
            using var q=Query(db,tx,"""
INSERT INTO oms_deribit_fix_report_incidents_v1(tenant_id,venue_id,environment,account_id,subaccount_id,incident_id,schema_version,reason,candidate_json,candidate_sha256)
VALUES(@t,@v,@e,@a,@s,@id,1,@reason,@json,@sha)
""");q.Parameters.AddWithValue("id",Guid.NewGuid());q.Parameters.AddWithValue("reason",reason);q.Parameters.AddWithValue("json",NpgsqlDbType.Jsonb,encoded.Json);q.Parameters.AddWithValue("sha",encoded.Sha256);q.ExecuteNonQuery();
        }
        void EnsureCapacity()
        {
            using var count=Query(db,tx,"SELECT (SELECT count(*) FROM oms_deribit_fix_reports_v1 WHERE "+AccountWhere+")+(SELECT count(*) FROM oms_deribit_fix_report_incidents_v1 WHERE "+AccountWhere+")");
            if((long)count.ExecuteScalar()!>=10000)throw new InvalidOperationException("Native FIX journal capacity exceeded.");
        }
    }
}
