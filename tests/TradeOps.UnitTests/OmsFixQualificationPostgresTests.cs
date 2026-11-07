using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class OmsFixQualificationPostgresTests
{
    [Fact]
    public async Task StageLeaseIsExclusiveAndUnresolvedBudgetSurvivesLeaseRestart()
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();var original=DeribitFixCommandTests.Command();
        var scope=new AccountKey(original.Scope.Tenant,original.Scope.Venue,OmsEnvironment.Mainnet,original.Scope.Account);
        var current=DeribitRiskInputsTests.Baseline() with{Scope=scope,UsableThroughUtc=DateTimeOffset.UtcNow.AddMinutes(1)};
        await using(var lease=await OmsPostgresFixQualificationLeaseV1.AcquireAsync(db.Source,scope,default))
        {
            Assert.Equal(0,await lease.CheckBudgetAsync(current,default));
            await Assert.ThrowsAsync<InvalidOperationException>(()=>OmsPostgresFixQualificationLeaseV1.AcquireAsync(db.Source,scope,default));
            var command=original with{Scope=scope,Payload=original.Payload with{Provenance=DeribitFixQualificationScenarioV1.Provenance,OperatorOrStrategyId="owner-fix-qualification"}};
            Assert.Equal(OmsJournalWriteResult.Created,await new OmsPostgresJournal(db.Source).PersistAsync(command));
            await Assert.ThrowsAsync<InvalidOperationException>(()=>lease.CheckBudgetAsync(current,default));
        }
        await using var restarted=await OmsPostgresFixQualificationLeaseV1.AcquireAsync(db.Source,scope,default);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>restarted.CheckBudgetAsync(current,default));
        Assert.Single((await new OmsPostgresRecoveryLedgerReader(db.Source).ReadAsync(scope)).Commands);
        await Assert.ThrowsAsync<ArgumentException>(()=>OmsPostgresFixQualificationLeaseV1.AcquireAsync(db.Source,original.Scope,default));
    }
    [Fact]
    public async Task VersionTwoGAndVersionOneMessagesShareDurableSequenceWithoutRewritingOldRows()
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();var key=OmsFixSessionStorePostgresTests.Key();await OmsFixSessionStorePostgresTests.Seed(db.Source,key);
        using(var store=new OmsPostgresFixSessionStoreV1(db.Source,key))
        {store.SaveAndAdvance(1,"0",null);store.SaveAndAdvance(2,"G","35=G\u000141=native-server\u000144=1700\u0001");store.SaveAndAdvance(3,"F","35=F\u000141=native-server\u0001");}
        using var next=new OmsPostgresFixSessionStoreV1(db.Source,key);Assert.Equal(4UL,next.NextSender);
        Assert.Equal(new[]{"0","G","F"},next.Read(1,3).Select(x=>x.MessageType));
        Assert.Equal(1,await OmsExecutionGatewayPostgresTests.Count(db,"oms_fix_amend_messages_v2"));
        Assert.Equal(2,await OmsExecutionGatewayPostgresTests.Count(db,"oms_fix_outbound_messages_v1"));
        foreach(var sql in new[]{"DELETE FROM oms_fix_amend_messages_v2","TRUNCATE oms_fix_amend_messages_v2","UPDATE oms_fix_amend_messages_v2 SET application_message='changed'"})
        {await using var q=db.Source.CreateCommand(sql);await Assert.ThrowsAsync<Npgsql.PostgresException>(()=>q.ExecuteNonQueryAsync());}
    }
}
