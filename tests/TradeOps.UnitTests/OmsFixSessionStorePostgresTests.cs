using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;

namespace TradeOps.UnitTests;
public sealed class OmsFixSessionStorePostgresTests
{
    internal static OmsFixSessionKeyV1 Key(string sender="tradeops-test")=>new(1,Scope(),sender,"DERIBITSERVER");
    internal static async Task Seed(NpgsqlDataSource source,OmsFixSessionKeyV1 key)
    {
        var hash=OmsRecoveryRunCodec.Encode(key).Sha256;
        await using var q=source.CreateCommand("""
INSERT INTO oms_fix_sessions_v1(tenant_id,venue_id,environment,account_id,subaccount_id,sender_comp_id,target_comp_id,schema_version,key_sha256)
VALUES(@t,@v,@e,@a,@s,@sender,@target,1,@hash)
""");
        q.Parameters.AddWithValue("t",key.Scope.Tenant.Value);q.Parameters.AddWithValue("v",key.Scope.Venue.Value);q.Parameters.AddWithValue("e",(short)key.Scope.Environment);
        q.Parameters.AddWithValue("a",key.Scope.Account.Value);q.Parameters.AddWithValue("s",key.Scope.Subaccount?.Value??"");q.Parameters.AddWithValue("sender",key.SenderCompId);
        q.Parameters.AddWithValue("target",key.TargetCompId);q.Parameters.AddWithValue("hash",hash);await q.ExecuteNonQueryAsync();
    }
    [Fact]
    public async Task ProvisioningIsRequiredAndRestartKeepsAtomicSenderAndAuthenticationTimestamp()
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();var key=Key();Assert.Throws<InvalidOperationException>(()=>new OmsPostgresFixSessionStoreV1(db.Source,key));
        await Seed(db.Source,key);DateTime created;
        using(var first=new OmsPostgresFixSessionStoreV1(db.Source,key))
        {created=first.CreationTimeUtc;first.SaveAndAdvance(1,"A",null);Assert.Equal(2UL,first.NextSender);Assert.Equal(1000,first.ReserveLogonTimestamp(1000));first.AdvanceTarget(5);}
        using var next=new OmsPostgresFixSessionStoreV1(db.Source,key);Assert.Equal(created,next.CreationTimeUtc);Assert.Equal(2UL,next.NextSender);Assert.Equal(5UL,next.NextTarget);
        Assert.Equal(1001,next.ReserveLogonTimestamp(900));Assert.Null(Assert.Single(next.Read(1,1)).ApplicationMessage);
        Assert.Throws<InvalidOperationException>(()=>next.AdvanceTarget(1));Assert.Throws<InvalidOperationException>(()=>next.AdvanceSender(1));
        Assert.Throws<InvalidOperationException>(()=>new OmsPostgresFixSessionStoreV1(db.Source,Key("another-sender")));
    }
    [Fact]
    public async Task ConcurrentSendersCannotOverwriteOrDoubleAdvanceTheSameSequence()
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();var key=Key();await Seed(db.Source,key);
        using var a=new OmsPostgresFixSessionStoreV1(db.Source,key);using var b=new OmsPostgresFixSessionStoreV1(db.Source,key);
        var outcomes=await Task.WhenAll(new[]{a,b}.Select(s=>Task.Run(()=>{try{s.SaveAndAdvance(1,"0",null);return true;}catch(InvalidOperationException){return false;}})));
        Assert.Single(outcomes,x=>x);Assert.Equal(2UL,a.NextSender);Assert.Single(a.Read(1,1));
        await using var q=db.Source.CreateCommand("UPDATE oms_fix_sessions_v1 SET next_sender=1");await Assert.ThrowsAsync<PostgresException>(()=>q.ExecuteNonQueryAsync());
    }
    [Fact]
    public async Task AuthenticationMaterialAndNonAtomicMutationAreRefusedAndAuditCannotBeErased()
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();var key=Key();await Seed(db.Source,key);using var store=new OmsPostgresFixSessionStoreV1(db.Source,key);
        Assert.Throws<ArgumentException>(()=>store.SaveAndAdvance(1,"A","553=must-never-persist\u0001"));
        Assert.Throws<ArgumentException>(()=>store.SaveAndAdvance(1,"D","35=D\u0001554=must-never-persist\u0001"));
        Assert.Equal(1UL,store.NextSender);store.SaveAndAdvance(1,"0",null);
        foreach(var sql in new[]{"DELETE FROM oms_fix_outbound_messages_v1","TRUNCATE oms_fix_outbound_messages_v1","DELETE FROM oms_fix_sessions_v1","UPDATE oms_fix_sessions_v1 SET sender_comp_id='repinned'"})
        {await using var q=db.Source.CreateCommand(sql);await Assert.ThrowsAsync<PostgresException>(()=>q.ExecuteNonQueryAsync());}
        Assert.Single(store.Read(1,1));
    }
}
