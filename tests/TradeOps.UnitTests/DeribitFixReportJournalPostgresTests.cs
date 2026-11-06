using System.Text;
using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit.Fix;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;

namespace TradeOps.UnitTests;
public sealed class DeribitFixReportJournalPostgresTests
{
    internal static DeribitFixReportEnvelopeV1 Envelope(OmsFixSessionKeyV1 key)
        =>new(1,DeribitFixCommandTests.Ack(DeribitFixCommandTests.Command(),key),null);
    private static async Task<long> Count(NpgsqlDataSource source,string table)
    {await using var q=source.CreateCommand("SELECT count(*) FROM "+table);return(long)(await q.ExecuteScalarAsync())!;}

    [Fact]
    public async Task ReplayAcrossSequenceEpochAndConnectionKeepsObservationsButNotAnotherNativeEvent()
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();var key=OmsFixSessionStorePostgresTests.Key();await OmsFixSessionStorePostgresTests.Seed(db.Source,key);
        var journal=new OmsPostgresDeribitFixReportJournalV1(db.Source,key);var input=Envelope(key);
        Assert.Equal(DeribitFixReportWriteResult.Inserted,journal.Record(input));Assert.Equal(DeribitFixReportWriteResult.Duplicate,journal.Record(input));
        var replay=input with{Execution=input.Execution! with{ConnectionEpoch=Guid.NewGuid(),Sequence=3,PossDup=true,ExecId="another-observer-exec-id",ReceivedAtUtc=DateTimeOffset.UtcNow}};
        Assert.Equal(DeribitFixReportWriteResult.ObservedNativeDuplicate,journal.Record(replay));
        var other=key with{SenderCompId="second-observed-connection"};await OmsFixSessionStorePostgresTests.Seed(db.Source,other);
        Assert.Equal(DeribitFixReportWriteResult.ObservedNativeDuplicate,new OmsPostgresDeribitFixReportJournalV1(db.Source,other).Record(replay with{Execution=replay.Execution! with{Session=other}}));
        Assert.Equal(3,await Count(db.Source,"oms_deribit_fix_reports_v1"));Assert.Equal(1,await Count(db.Source,"oms_deribit_fix_native_events_v1"));
        Assert.Equal(0,await Count(db.Source,"oms_deribit_command_receipts_v1"));Assert.Equal(0,await Count(db.Source,"oms_inbox"));
        await using var version=db.Source.CreateCommand("SELECT version FROM oms_evidence_versions_v1");Assert.Equal(3L,await version.ExecuteScalarAsync());
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task ConflictingObservationOrNativeEventRetainsImmutableIncidentAndAdvancesEconomicEvidence(bool newSequence)
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();var key=OmsFixSessionStorePostgresTests.Key();await OmsFixSessionStorePostgresTests.Seed(db.Source,key);
        var journal=new OmsPostgresDeribitFixReportJournalV1(db.Source,key);var input=Envelope(key);Assert.Equal(DeribitFixReportWriteResult.Inserted,journal.Record(input));
        var collision=input with{Execution=input.Execution! with{Price=input.Execution.Price+1,Sequence=newSequence?3UL:input.Execution.Sequence}};
        Assert.Equal(DeribitFixReportWriteResult.Collision,journal.Record(collision));Assert.Equal(1,await Count(db.Source,"oms_deribit_fix_reports_v1"));
        Assert.Equal(1,await Count(db.Source,"oms_deribit_fix_report_incidents_v1"));
        await using(var q=db.Source.CreateCommand("SELECT reason,candidate_json::text,candidate_sha256 FROM oms_deribit_fix_report_incidents_v1"))
        {await using var rows=await q.ExecuteReaderAsync();Assert.True(await rows.ReadAsync());Assert.Equal(newSequence?"NativeEventCollision":"ObservationCollision",rows.GetString(0));
            var retained=OmsRecoveryRunCodec.Decode<DeribitFixReportEnvelopeV1>(rows.GetString(1),rows.GetFieldValue<byte[]>(2));Assert.Equal(collision.Execution.Price,retained.Execution!.Price);}
        foreach(var table in new[]{"oms_deribit_fix_reports_v1","oms_deribit_fix_native_events_v1","oms_deribit_fix_report_incidents_v1"})
        {await using var q=db.Source.CreateCommand("TRUNCATE "+table+" CASCADE");await Assert.ThrowsAsync<PostgresException>(()=>q.ExecuteNonQueryAsync());}
        await using var version=db.Source.CreateCommand("SELECT version FROM oms_evidence_versions_v1");Assert.Equal(2L,await version.ExecuteScalarAsync());
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task ActualEngineRetainsNativeReportBeforeTargetCursorAndLostCommitAckCannotEraseIt(bool loseCallbackAck)
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();var key=OmsFixSessionStorePostgresTests.Key("engine-"+Guid.NewGuid().ToString("N"));await OmsFixSessionStorePostgresTests.Seed(db.Source,key);
        var journal=new OmsPostgresDeribitFixReportJournalV1(db.Source,key);using var protocol=new OmsPostgresFixSessionStoreV1(db.Source,key);
        DeribitFixReportEnvelopeV1? retained=null;
        using(var engine=new DeribitQuickFixSessionV1(new(key),protocol,new OmsPhysicalSessionAuthority(write=>write()),"synthetic-client","synthetic-secret",
            _=>Task.CompletedTask,(raw,epoch,received)=>
            {
                var parsed=DeribitFixReportParserV1.Execution(DeribitQuickFixSessionTests.Parse(raw),key,epoch,received);retained=new(1,parsed,null);
                Assert.Equal(DeribitFixReportWriteResult.Inserted,journal.Record(retained));Assert.Equal(2UL,protocol.NextTarget);
                if(loseCallbackAck)throw new IOException("Synthetic failure after native report commit.");
            },()=>{},()=>{}))
        {
            using var peer=new DeribitQuickFixSessionTests.Rig();
            string Frame(string type,ulong sequence,params(int,string)[] values)
            {var m=DeribitQuickFixSessionTests.Parse(peer.Peer(type,sequence,values));m.Header.SetField(new QuickFix.Fields.TargetCompID(key.SenderCompId));return m.ConstructString();}
            engine.Tick();engine.Receive(Encoding.ASCII.GetBytes(Frame("A",1,(98,"0"),(108,"30"),(141,"N"))));
            var raw=Frame("8",2,(37,"native-1"),(11,"server-id"),(41,"original-id"),(527,"change-1"),(39,"0"),(54,"1"),
                (60,DateTime.UtcNow.ToString("yyyyMMdd-HH:mm:ss.fff",System.Globalization.CultureInfo.InvariantCulture)),(151,"1"),(14,"0"),(38,"1"),(152,"1"),
                (40,"2"),(44,"2000"),(103,"0"),(55,"ETH-PERPETUAL"),(231,"1"),(854,"1"));
            if(loseCallbackAck)Assert.Throws<InvalidOperationException>(()=>engine.Receive(Encoding.ASCII.GetBytes(raw)));
            else engine.Receive(Encoding.ASCII.GetBytes(raw));
            Assert.Equal(loseCallbackAck?2UL:3UL,protocol.NextTarget);Assert.Equal(1,await Count(db.Source,"oms_deribit_fix_reports_v1"));
        }
        using var restarted=new OmsPostgresFixSessionStoreV1(db.Source,key);Assert.Equal(loseCallbackAck?2UL:3UL,restarted.NextTarget);
        Assert.NotNull(retained);Assert.Equal(DeribitFixReportWriteResult.Duplicate,journal.Record(retained));
    }
    [Fact]
    public async Task CorruptEventDigestAndWrongAccountCannotMasqueradeAsSuccessfulDedup()
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();var key=OmsFixSessionStorePostgresTests.Key();await OmsFixSessionStorePostgresTests.Seed(db.Source,key);
        var journal=new OmsPostgresDeribitFixReportJournalV1(db.Source,key);var input=Envelope(key);journal.Record(input);
        await using(var corrupt=db.Source.CreateCommand("""
ALTER TABLE oms_deribit_fix_native_events_v1 DISABLE TRIGGER tr_fix_native_immutable;
UPDATE oms_deribit_fix_native_events_v1 SET semantic_sha256=decode(repeat('01',32),'hex');
ALTER TABLE oms_deribit_fix_native_events_v1 ENABLE TRIGGER tr_fix_native_immutable;
"""))await corrupt.ExecuteNonQueryAsync();
        Assert.Throws<InvalidOperationException>(()=>journal.Record(input with{Execution=input.Execution! with{Sequence=3}}));
        var other=key with{Scope=TradeOps.UnitTests.Support.DeribitCaptureTestRig.Scope("foreign")};
        Assert.Throws<ArgumentException>(()=>journal.Record(input with{Execution=input.Execution! with{Session=other}}));
        Assert.Equal(1,await Count(db.Source,"oms_deribit_fix_reports_v1"));
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task OldPossDupStillValidatesNativePayloadEvenWhenEngineSkipsFromApp(bool changed)
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();var key=OmsFixSessionStorePostgresTests.Key("replay-"+Guid.NewGuid().ToString("N"));
        await OmsFixSessionStorePostgresTests.Seed(db.Source,key);
        var journal=new OmsPostgresDeribitFixReportJournalV1(db.Source,key);using var protocol=new OmsPostgresFixSessionStoreV1(db.Source,key);
        var results=new List<DeribitFixReportWriteResult>();
        using var engine=new DeribitQuickFixSessionV1(new(key),protocol,new OmsPhysicalSessionAuthority(write=>write()),"synthetic-client","synthetic-secret",
            _=>Task.CompletedTask,(raw,epoch,received)=>
            {
                var result=journal.Record(new(1,DeribitFixReportParserV1.Execution(DeribitQuickFixSessionTests.Parse(raw),key,epoch,received),null));
                results.Add(result);if(result==DeribitFixReportWriteResult.Collision)throw new InvalidOperationException("Native collision.");
            },()=>{},()=>{});
        using var peer=new DeribitQuickFixSessionTests.Rig();
        string Frame(string type,ulong sequence,params(int,string)[] values)
        {var m=DeribitQuickFixSessionTests.Parse(peer.Peer(type,sequence,values));m.Header.SetField(new QuickFix.Fields.TargetCompID(key.SenderCompId));return m.ConstructString();}
        engine.Tick();engine.Receive(Encoding.ASCII.GetBytes(Frame("A",1,(98,"0"),(108,"30"),(141,"N"))));
        var text=Frame("8",2,(37,"native-1"),(11,"server-id"),(41,"original-id"),(527,"change-1"),(39,"0"),(54,"1"),
            (60,DateTime.UtcNow.ToString("yyyyMMdd-HH:mm:ss.fff",System.Globalization.CultureInfo.InvariantCulture)),(151,"1"),(14,"0"),(38,"1"),(152,"1"),
            (40,"2"),(44,"2000"),(103,"0"),(55,"ETH-PERPETUAL"),(231,"1"),(854,"1"));
        engine.Receive(Encoding.ASCII.GetBytes(text));Assert.Equal(3UL,protocol.NextTarget);
        var replay=DeribitQuickFixSessionTests.Parse(text);replay.Header.SetField(new QuickFix.Fields.PossDupFlag(true));
        replay.Header.SetField(new QuickFix.Fields.OrigSendingTime(replay.Header.GetDateTime(52)));
        if(changed)replay.SetField(new QuickFix.Fields.Price(2001));
        if(changed)Assert.Throws<InvalidOperationException>(()=>engine.Receive(Encoding.ASCII.GetBytes(replay.ConstructString())));
        else engine.Receive(Encoding.ASCII.GetBytes(replay.ConstructString()));
        Assert.Equal(new[]{DeribitFixReportWriteResult.Inserted,changed?DeribitFixReportWriteResult.Collision:DeribitFixReportWriteResult.Duplicate},results);
        Assert.Equal(3UL,protocol.NextTarget);Assert.Equal(1,await Count(db.Source,"oms_deribit_fix_reports_v1"));
        Assert.Equal(changed?1:0,await Count(db.Source,"oms_deribit_fix_report_incidents_v1"));
    }
}
