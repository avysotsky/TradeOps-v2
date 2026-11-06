using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class OmsFixRuntimeRolePostgresTests
{
    [Theory]
    [InlineData("valid")][InlineData("seed")][InlineData("policy")][InlineData("identity")][InlineData("missing")]
    public async Task RealRestrictedProtocolIdentityCanRetainNativeEvidenceButCannotProvisionOrAcquireExecutionRights(string scenario)
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();var key=OmsFixSessionStorePostgresTests.Key();
        await OmsFixSessionStorePostgresTests.Seed(db.Source,key);
        var role="to_fix_"+Guid.NewGuid().ToString("N");
        await using(var q=db.Source.CreateCommand($"""
CREATE ROLE "{role}" NOLOGIN; COMMENT ON ROLE "{role}" IS 'tradeops-tests:role:v1:{db.Name}';
GRANT USAGE ON SCHEMA public TO "{role}";
GRANT SELECT ON oms_fix_sessions_v1,oms_fix_outbound_messages_v1,oms_deribit_fix_reports_v1,
 oms_deribit_fix_native_events_v1,oms_deribit_fix_report_incidents_v1,oms_evidence_versions_v1 TO "{role}";
GRANT INSERT ON oms_fix_outbound_messages_v1,oms_deribit_fix_reports_v1,
 oms_deribit_fix_native_events_v1,oms_deribit_fix_report_incidents_v1 TO "{role}";
GRANT UPDATE(next_sender,next_target,last_logon_ms) ON oms_fix_sessions_v1 TO "{role}";
GRANT INSERT(tenant_id,venue_id,environment,account_id,subaccount_id) ON oms_evidence_versions_v1 TO "{role}";
"""))await q.ExecuteNonQueryAsync();
        try
        {
            var mutation=scenario switch {
                "seed"=>$"GRANT INSERT ON oms_fix_sessions_v1 TO \"{role}\"",
                "policy"=>$"GRANT UPDATE(revision) ON oms_account_risk_policies_v1 TO \"{role}\"",
                "identity"=>$"GRANT UPDATE(key_sha256) ON oms_fix_sessions_v1 TO \"{role}\"",
                "missing"=>$"REVOKE INSERT ON oms_deribit_fix_reports_v1 FROM \"{role}\"",_=>null};
            if(mutation is not null){await using var q=db.Source.CreateCommand(mutation);await q.ExecuteNonQueryAsync();}
            await using var source=NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(db.ConnectionString){Pooling=false,Options="-c role="+role}.ConnectionString);
            if(scenario!="valid")
            {await Assert.ThrowsAsync<InvalidOperationException>(()=>OmsPostgresFixRuntimeGuardV1.ValidateAsync(source));return;}
            await OmsPostgresFixRuntimeGuardV1.ValidateAsync(source);
            using var store=new OmsPostgresFixSessionStoreV1(source,key);
            store.SaveAndAdvance(1,"0",null);store.AdvanceTarget(2);Assert.Equal(2UL,store.NextSender);Assert.Equal(2UL,store.NextTarget);
            Assert.True(store.ReserveLogonTimestamp(1000)>=1000);
            var journal=new OmsPostgresDeribitFixReportJournalV1(source,key);
            var report=DeribitFixReportJournalPostgresTests.Envelope(key);
            Assert.Equal(DeribitFixReportWriteResult.Inserted,journal.Record(report));
            Assert.Equal(DeribitFixReportWriteResult.Duplicate,journal.Record(report));
            Assert.Equal(DeribitFixReportWriteResult.Collision,journal.Record(report with{Execution=report.Execution! with{Price=report.Execution.Price+1}}));
            await Assert.ThrowsAsync<InvalidOperationException>(()=>OmsPostgresExecutionRuntimeGuard.ValidateAsync(source));
            await Assert.ThrowsAsync<InvalidOperationException>(()=>OmsPostgresFixRuntimeGuardV1.ValidateAsync(db.Source));
            foreach(var sql in new[]{"UPDATE oms_fix_sessions_v1 SET key_sha256=key_sha256","DELETE FROM oms_deribit_fix_reports_v1","UPDATE oms_commands SET updated_at=clock_timestamp()","INSERT INTO oms_fix_sessions_v1 SELECT * FROM oms_fix_sessions_v1"})
            {await using var q=source.CreateCommand(sql);Assert.Equal("42501",(await Assert.ThrowsAsync<PostgresException>(()=>q.ExecuteNonQueryAsync())).SqlState);}
        }
        finally
        {
            await using var q=db.Source.CreateCommand($"DROP OWNED BY \"{role}\"; DROP ROLE \"{role}\"");await q.ExecuteNonQueryAsync();
        }
    }
}
