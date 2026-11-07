using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using TradeOps.Domain.Oms;
using TradeOps.Infrastructure.Persistence;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;

namespace TradeOps.UnitTests;
public sealed class OmsFixProvisioningPlanTests
{
    private const string Retained="20261104120000_AddOmsPrivateCaptureObservations";
    private static AccountKey Scope(bool sub)=>new(new("tenant"),new("DERIBIT"),OmsEnvironment.Mainnet,new("account"),sub?new TradingSubaccountId("account"):null);
    private static TradeOpsDbContext Context(string connection)=>new(new DbContextOptionsBuilder<TradeOpsDbContext>().UseNpgsql(connection).Options);
    [Theory]
    [InlineData("unknown-prefix")][InlineData("missing-middle")][InlineData("current")][InlineData("delimiter")]
    public void OfflineReviewRefusesIncompleteHistoryOrUnsafeTargetWithoutOpeningDatabase(string mode)
    {
        using var context=Context("Host=127.0.0.1;Port=1;Database=offline;Username=offline;Timeout=1");var known=context.Database.GetMigrations().ToArray();
        var applied=known.TakeWhile(x=>string.CompareOrdinal(x,Retained)<=0).ToArray();Assert.Equal(20,applied.Length);
        applied=mode switch{"unknown-prefix"=>["unrecognised"],"missing-middle"=>applied.Where((_,i)=>i!=2).ToArray(),"current"=>known,_=>applied};
        Assert.Throws<ArgumentException>(()=>OmsFixProvisioningReviewPlan.Generate(applied,Scope(true),"review",mode=="delimiter"?"$guard$":"marked","cluster"));
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task RetainedUpgradePreservesJournalAndGeneratedFourRolesPassActualRuntimeGuards(bool sub)
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();await using var context=Context(db.ConnectionString);
        await context.GetService<IMigrator>().MigrateAsync(Retained);var applied=(await context.Database.GetAppliedMigrationsAsync()).ToArray();Assert.Equal(20,applied.Length);
        var scope=Scope(sub);var journal=new OmsPostgresJournal(db.Source);var capture=new OmsPostgresPrivateEventCapture(db.Source);
        for(var i=0;i<2;i++)
        {
            var command=OmsExecutionGatewayPostgresTests.Command() with{Scope=scope};await journal.PersistAsync(command);
            await journal.MarkDispatchStartedAsync(scope,command.CommandId);
            var item=OmsPrivateEventContractTests.Execution(scope);item=item with{Facts=item.Facts with{ExchangeTradeId="retained-"+i}};await capture.CaptureAsync(item);
        }
        async Task<string[]> RetainedRows()
        {
            var rows=new List<string>();foreach(var table in new[]{"oms_commands","oms_attempts","oms_inbox","oms_private_capture_observations"})
            {await using var q=db.Source.CreateCommand($"SELECT count(*) FROM {table}");Assert.Equal(2L,await q.ExecuteScalarAsync());
                await using var content=db.Source.CreateCommand($"SELECT jsonb_agg(doc ORDER BY doc::text)::text FROM (SELECT to_jsonb(t) doc FROM {table} t) x");rows.Add((string)(await content.ExecuteScalarAsync())!);}
            return rows.ToArray();
        }
        var before=await RetainedRows();var plan=OmsFixProvisioningReviewPlan.Generate(applied,scope,db.Name,"tradeops-tests:database:v1:"+db.Name,"tradeops_tests_v1");
        Assert.Equal(sub?2:1,plan.Session.SchemaVersion);Assert.Equal(19,plan.MigrationCount);
        var wrongTarget=OmsFixProvisioningReviewPlan.Generate(applied,scope,"foreign_database","tradeops-tests:database:v1:"+db.Name,"tradeops_tests_v1");
        await using(var q=db.Source.CreateCommand(wrongTarget.MigrationSql))await Assert.ThrowsAsync<PostgresException>(()=>q.ExecuteNonQueryAsync());
        await using(var connection=await db.Source.OpenConnectionAsync())
        {
            await using var tx=await connection.BeginTransactionAsync();
            await using(var q=new NpgsqlCommand("INSERT INTO \"__EFMigrationsHistory\" VALUES('unreviewed','10.0.0')",connection,tx))await q.ExecuteNonQueryAsync();
            await using(var q=new NpgsqlCommand(plan.MigrationSql,connection,tx))await Assert.ThrowsAsync<PostgresException>(()=>q.ExecuteNonQueryAsync());
            await tx.RollbackAsync();
        }
        Assert.Equal(before,await RetainedRows());
        await using(var q=db.Source.CreateCommand(plan.MigrationSql))await q.ExecuteNonQueryAsync();
        Assert.Equal(before,await RetainedRows());
        // Replaying the migration script is idempotent; session/role provisioning intentionally is not.
        await using(var q=db.Source.CreateCommand(plan.MigrationSql))await q.ExecuteNonQueryAsync();Assert.Equal(before,await RetainedRows());
        Assert.Equal(context.Database.GetMigrations(),await context.Database.GetAppliedMigrationsAsync());
        var names=OmsFixProvisioningReviewPlan.Roles.Select(_=>"to_fixplan_"+Guid.NewGuid().ToString("N")).ToArray();var sql=plan.RolesAndSessionSql;
        for(var i=0;i<names.Length;i++)sql=sql.Replace(OmsFixProvisioningReviewPlan.Roles[i],names[i],StringComparison.Ordinal);
        sql=sql.Replace("tradeops-fix-qualification:runtime:v1","tradeops-tests:role:v1:"+db.Name,StringComparison.Ordinal);
        var sources=new List<NpgsqlDataSource>();
        try
        {
            await using(var q=db.Source.CreateCommand(sql))await q.ExecuteNonQueryAsync();
            foreach(var name in names)
            {
                var password=Guid.NewGuid().ToString("N");await using(var q=db.Source.CreateCommand($"ALTER ROLE \"{name}\" LOGIN PASSWORD '{password}'"))await q.ExecuteNonQueryAsync();
                sources.Add(NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(db.ConnectionString){Username=name,Password=password,Options="",Pooling=false}.ConnectionString));
            }
            await OmsPostgresPrivateCaptureOwner.ValidateRoleAsync(sources[0]);await OmsPostgresRecoveryRuntimeGuard.ValidateAsync(sources[1]);
            await OmsPostgresExecutionRuntimeGuard.ValidateAsync(sources[2],scope:scope);await OmsPostgresFixRuntimeGuardV1.ValidateAsync(sources[3]);
            using var store=new OmsPostgresFixSessionStoreV1(sources[3],plan.Session);store.SaveAndAdvance(1,"0",null);Assert.Equal(2UL,store.NextSender);
            await using(var q=db.Source.CreateCommand(sql))await Assert.ThrowsAsync<PostgresException>(()=>q.ExecuteNonQueryAsync());
            Assert.Equal(2UL,store.NextSender);Assert.Equal(before,await RetainedRows());
            foreach(var table in new[]{"oms_account_risk_policies_v1","oms_execution_bindings_v2"})
            {await using var q=db.Source.CreateCommand($"SELECT count(*) FROM {table}");Assert.Equal(0L,await q.ExecuteScalarAsync());}
        }
        finally
        {
            foreach(var source in sources)await source.DisposeAsync();
            foreach(var name in names)
            {await using var exists=db.Source.CreateCommand("SELECT EXISTS(SELECT 1 FROM pg_roles WHERE rolname=@n)");exists.Parameters.AddWithValue("n",name);
                if(await exists.ExecuteScalarAsync() is true){await using var q=db.Source.CreateCommand($"DROP OWNED BY \"{name}\"; DROP ROLE \"{name}\"");await q.ExecuteNonQueryAsync();}}
        }
    }
}
