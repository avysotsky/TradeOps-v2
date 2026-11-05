using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Domain.Enums;
using TradeOps.Domain.Oms;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DeribitBoundedOmsAdmissionPostgresTests
{
    internal static readonly AccountKey Scope = new(new("tenant"), new("DERIBIT"), OmsEnvironment.Mainnet, new("synthetic-account"));
    internal static OmsJournalCommandRequest Request() => new(Scope, new(Guid.NewGuid()), new(Guid.NewGuid()), new(Guid.NewGuid()), new(0),
        new(OmsJournalCommandKind.Place, "ETH-PERPETUAL", OrderSide.Buy, OrderType.Limit, 1, OmsQuantityUnit.QuoteAsset,
            2250, null, "good_til_cancelled", false, true, DeribitBoundedQualificationPolicy.Provenance(DeribitBoundedQualificationPolicy.CurrentStage),
            DeribitBoundedQualificationPolicy.Operator, "bounded-usd1-distance10-v1", DateTimeOffset.UtcNow));

    [Fact]
    public async Task StageQuotaSurvivesLeaseAndSourceRecreationWithoutDeletingAnyCommand()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(pooling: false);
        var reader = new OmsPostgresRecoveryLedgerReader(db.Source);
        for (var i = 1; i <= 3; i++)
        {
            await using var recreated = NpgsqlDataSource.Create(db.ConnectionString);
            await using var lease = await OmsPostgresBoundedQualificationLease.AcquireAsync(recreated, new OmsPostgresRecoveryLedgerReader(recreated), Scope);
            Assert.Equal(i, await lease.ReserveAsync(DeribitBoundedQualificationPolicy.CurrentStage, Request()));
        }
        await using var final = await OmsPostgresBoundedQualificationLease.AcquireAsync(db.Source, reader, Scope);
        await Assert.ThrowsAsync<InvalidOperationException>(() => final.ReserveAsync(DeribitBoundedQualificationPolicy.CurrentStage, Request()));
        Assert.Equal(3, (await reader.ReadAsync(Scope)).Commands.Count);
    }

    [Fact]
    public async Task ConcurrentQualificationScopeCannotAcquireSameAdmissionLease()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(pooling: false);
        var reader = new OmsPostgresRecoveryLedgerReader(db.Source);
        await using var first = await OmsPostgresBoundedQualificationLease.AcquireAsync(db.Source, reader, Scope);
        await Assert.ThrowsAsync<InvalidOperationException>(() => OmsPostgresBoundedQualificationLease.AcquireAsync(db.Source, reader, Scope));
    }

    [Fact]
    public async Task StartedPlacementWithoutDirectReceiptBlocksNextStageAttempt()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var reader = new OmsPostgresRecoveryLedgerReader(db.Source);
        var request = Request();
        await using (var first = await OmsPostgresBoundedQualificationLease.AcquireAsync(db.Source, reader, Scope))
            await first.ReserveAsync(DeribitBoundedQualificationPolicy.CurrentStage, request);
        await new OmsPostgresJournal(db.Source).MarkDispatchStartedAsync(Scope, request.CommandId);
        await using var next = await OmsPostgresBoundedQualificationLease.AcquireAsync(db.Source, reader, Scope);
        await Assert.ThrowsAsync<InvalidOperationException>(() => next.ReserveAsync(DeribitBoundedQualificationPolicy.CurrentStage, Request()));
        Assert.Single((await reader.ReadAsync(Scope)).Commands);
    }

    [Theory]
    [InlineData("B4-new-random-stage")] [InlineData("not-a-stage")]
    public async Task CallerCannotResetQuotaByInventingNewStageId(string stage)
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var reader = new OmsPostgresRecoveryLedgerReader(db.Source);
        await using var lease = await OmsPostgresBoundedQualificationLease.AcquireAsync(db.Source, reader, Scope);
        await Assert.ThrowsAsync<ArgumentException>(() => lease.ReserveAsync(stage, Request()));
        Assert.Empty((await reader.ReadAsync(Scope)).Commands);
    }

    [Fact]
    public async Task DroppedLeaseSessionCannotPassSendHealthCheck()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(pooling: false);
        // Terminate only this case's uniquely named admission connection in its disposable clone.
        var builder = new NpgsqlConnectionStringBuilder(db.ConnectionString) { ApplicationName = "bounded-lease-" + Guid.NewGuid().ToString("N"), Pooling = false };
        await using var source = NpgsqlDataSource.Create(builder.ConnectionString);
        await using var lease = await OmsPostgresBoundedQualificationLease.AcquireAsync(source, new OmsPostgresRecoveryLedgerReader(db.Source), Scope);
        await using (var terminate = db.Source.CreateCommand("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname=current_database() AND application_name=@app"))
        { terminate.Parameters.AddWithValue("app", builder.ApplicationName); Assert.Equal(true, await terminate.ExecuteScalarAsync()); }
        await Assert.ThrowsAnyAsync<Exception>(() => lease.CheckAsync());
    }
}
