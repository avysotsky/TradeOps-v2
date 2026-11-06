using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;

namespace TradeOps.UnitTests;
public sealed class DeribitFixLedgerPostgresTests
{
    [Theory]
    [InlineData("binding")][InlineData("report")][InlineData("event")][InlineData("incident")]
    public async Task ReaderFailsClosedOnCorruptNativeEvidenceAndRetainsOriginalContracts(string corrupt)
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();var (binding,report)=DeribitFixEvidenceTests.Evidence();
        await OmsFixSessionStorePostgresTests.Seed(db.Source,binding.Session);
        var journal=new OmsPostgresJournal(db.Source);await journal.PersistAsync(binding.Command);await journal.MarkDispatchStartedAsync(Scope(),binding.Command.CommandId);
        await using(var c=await db.Source.OpenConnectionAsync())
        {await using var t=await c.BeginTransactionAsync();await OmsPostgresFixCommandEvidenceV1.InsertAsync(c,t,binding,CancellationToken.None);await t.CommitAsync();}
        var native=new OmsPostgresDeribitFixReportJournalV1(db.Source,binding.Session);native.Record(new(1,report,null));
        var ledger=await new OmsPostgresRecoveryLedgerReader(db.Source,true).ReadAsync(Scope());Assert.True(ledger.IsUsable);Assert.Single(DeribitNativeCommandEvidenceV1.Read(ledger));
        if(corrupt=="incident")native.Record(new(1,report with{Price=report.Price+1},null));
        else
        {
            var (table,trigger,column)=corrupt switch{
                "binding"=>("oms_deribit_fix_command_bindings_v1","tr_fix_binding_immutable","binding_sha256"),
                "report"=>("oms_deribit_fix_reports_v1","tr_fix_native_immutable","report_sha256"),
                _=>("oms_deribit_fix_native_events_v1","tr_fix_native_immutable","semantic_sha256")};
            await using var q=db.Source.CreateCommand($"ALTER TABLE {table} DISABLE TRIGGER {trigger}; UPDATE {table} SET {column}=decode(repeat('00',32),'hex'); ALTER TABLE {table} ENABLE TRIGGER {trigger}");
            await q.ExecuteNonQueryAsync();
        }
        var invalid=await new OmsPostgresRecoveryLedgerReader(db.Source,true).ReadAsync(Scope());
        Assert.False(invalid.IsUsable);Assert.Contains(OmsRecoveryLedgerProblem.InvalidRecord,invalid.Problems);
    }
    [Fact]
    public async Task NativeBindingAndReportReadsUseTheCallersRepeatableReadSnapshot()
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();var (binding,report)=DeribitFixEvidenceTests.Evidence();
        await OmsFixSessionStorePostgresTests.Seed(db.Source,binding.Session);
        var j=new OmsPostgresJournal(db.Source);await j.PersistAsync(binding.Command);await j.MarkDispatchStartedAsync(Scope(),binding.Command.CommandId);
        await using var reader=await db.Source.OpenConnectionAsync();await using var rr=await reader.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        var first=await OmsPostgresFixCommandEvidenceV1.ReadAsync(reader,rr,Scope(),100,DateTimeOffset.UtcNow,CancellationToken.None);Assert.Empty(first.Bindings);
        await using(var writer=await db.Source.OpenConnectionAsync())
        {await using var tx=await writer.BeginTransactionAsync();await OmsPostgresFixCommandEvidenceV1.InsertAsync(writer,tx,binding,CancellationToken.None);await tx.CommitAsync();}
        new OmsPostgresDeribitFixReportJournalV1(db.Source,binding.Session).Record(new(1,report,null));
        var same=await OmsPostgresFixCommandEvidenceV1.ReadAsync(reader,rr,Scope(),100,DateTimeOffset.UtcNow,CancellationToken.None);
        Assert.Empty(same.Bindings);Assert.Empty(same.Reports);await rr.CommitAsync();
        var current=await new OmsPostgresRecoveryLedgerReader(db.Source).ReadAsync(Scope());Assert.Single(current.FixBindings!);Assert.Single(current.FixReports!);
    }
}
