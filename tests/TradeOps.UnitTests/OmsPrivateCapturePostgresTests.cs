using Microsoft.EntityFrameworkCore;
using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Infrastructure.Persistence;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;
using static TradeOps.UnitTests.OmsPrivateEventContractTests;

namespace TradeOps.UnitTests;

public sealed class OmsPrivateCapturePostgresTests
{
    [Fact]
    public async Task ReconnectDedup_CommitsSeparateObservations_PreservesFirstFactsAndRetryIdentity()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        IOmsPrivateEventCapture store = new OmsPostgresPrivateEventCapture(db.Source);
        var first = Execution();
        Assert.Equal(OmsPrivateCaptureResult.Inserted, await store.CaptureAsync(first));
        var original = await Scalar(db.Source, "SELECT event_json::text || encode(payload_sha256,'hex') FROM oms_inbox");
        var replay = first with { Facts = first.Facts with { NativeAmount = 10.000m, Price = 65000.000m },
            Observation = new(Guid.NewGuid(), Guid.NewGuid(), Time.AddHours(1),
                OmsPrivateCaptureSource.RecoveryQuery, "private/get_user_trades_by_currency") };
        Assert.Equal(OmsPrivateCaptureResult.Duplicate, await store.CaptureAsync(replay));
        Assert.Equal(OmsPrivateCaptureResult.Duplicate, await new OmsPostgresPrivateEventCapture(db.Source).CaptureAsync(first));
        Assert.Equal(1L, await Count(db.Source, "oms_inbox"));
        Assert.Equal(2L, await Count(db.Source, "oms_private_capture_observations"));
        Assert.Equal(0L, await Count(db.Source, "oms_integrity_incidents"));
        Assert.Equal(original, await Scalar(db.Source, "SELECT event_json::text || encode(payload_sha256,'hex') FROM oms_inbox"));
        Assert.Equal(2L, await Scalar(db.Source, "SELECT count(DISTINCT stream_epoch) FROM oms_private_capture_observations"));
        Assert.Equal(2, await Scalar(db.Source, "SELECT schema_version FROM oms_inbox"));
        Assert.Equal(DBNull.Value, await Scalar(db.Source, "SELECT aggregate_id FROM oms_inbox"));
        await AssertNoProjection(db.Source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentReceipts_ExactlyOneFactAndIdempotentObservations(bool sameCapture)
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        var first = Execution();
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            new OmsPostgresPrivateEventCapture(db.Source).CaptureAsync(first with
            { Observation = sameCapture ? first.Observation : first.Observation with
                { CaptureId = Guid.NewGuid(), StreamEpoch = Guid.NewGuid() } })));
        Assert.Single(results, x => x == OmsPrivateCaptureResult.Inserted);
        Assert.Equal(11, results.Count(x => x == OmsPrivateCaptureResult.Duplicate));
        Assert.Equal(1L, await Count(db.Source, "oms_inbox"));
        Assert.Equal(sameCapture ? 1L : 12L, await Count(db.Source, "oms_private_capture_observations"));
        Assert.Equal(0L, await Count(db.Source, "oms_integrity_incidents"));
    }

    [Fact]
    public async Task ConcurrentConflictingCaptureId_OneAcceptedFactAndOneQuarantinedAlternative()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        var first = Execution();
        var other = first with { Facts = first.Facts with { ExchangeTradeId = "other-trade" } };
        var results = await Task.WhenAll(new OmsPostgresPrivateEventCapture(db.Source).CaptureAsync(first),
            new OmsPostgresPrivateEventCapture(db.Source).CaptureAsync(other));
        Assert.Single(results, x => x == OmsPrivateCaptureResult.Inserted);
        Assert.Single(results, x => x == OmsPrivateCaptureResult.Collision);
        Assert.Equal(1L, await Count(db.Source, "oms_inbox"));
        Assert.Equal(2L, await Count(db.Source, "oms_private_capture_observations"));
        Assert.Equal(1L, await Count(db.Source, "oms_integrity_incidents"));
    }

    [Fact]
    public async Task ConflictingExecution_IsQuarantinedWithEntireIncomingFacts_WithoutOverwritingFirst()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        var store = new OmsPostgresPrivateEventCapture(db.Source);
        var first = Execution();
        Assert.Equal(OmsPrivateCaptureResult.Inserted, await store.CaptureAsync(first));
        var conflict = first with { Facts = first.Facts with { NativeAmount = 11m },
            Observation = first.Observation with { CaptureId = Guid.NewGuid() } };
        Assert.Equal(OmsPrivateCaptureResult.Collision, await store.CaptureAsync(conflict));
        Assert.Equal(OmsPrivateCaptureResult.Collision, await store.CaptureAsync(conflict));
        Assert.Equal(1L, await Count(db.Source, "oms_inbox"));
        Assert.Equal(2L, await Count(db.Source, "oms_private_capture_observations"));
        Assert.Equal(1L, await Count(db.Source, "oms_integrity_incidents"));
        Assert.Equal(10m, await Scalar(db.Source, "SELECT (event_json->'facts'->>'nativeAmount')::numeric FROM oms_inbox"));
        Assert.Equal(11m, await Scalar(db.Source, """
SELECT (observation_json->'venueEvent'->'facts'->>'nativeAmount')::numeric
FROM oms_private_capture_observations WHERE disposition=3
"""));
    }

    [Fact]
    public async Task CaptureIdReuse_QuarantinesDifferentInputWithoutInsertingAnUnrelatedEvent()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        var store = new OmsPostgresPrivateEventCapture(db.Source);
        var first = Execution();
        Assert.Equal(OmsPrivateCaptureResult.Inserted, await store.CaptureAsync(first));
        var conflict = first with { Facts = first.Facts with { ExchangeTradeId = "unrelated-trade" } };
        Assert.Equal(OmsPrivateCaptureResult.Collision, await store.CaptureAsync(conflict));
        Assert.Equal(OmsPrivateCaptureResult.Collision, await store.CaptureAsync(conflict));
        Assert.Equal(OmsPrivateCaptureResult.Duplicate, await store.CaptureAsync(first));
        Assert.Equal(1L, await Count(db.Source, "oms_inbox"));
        Assert.Equal(2L, await Count(db.Source, "oms_private_capture_observations"));
        Assert.Equal(1L, await Count(db.Source, "oms_integrity_incidents"));
        Assert.Equal("unrelated-trade", await Scalar(db.Source, """
SELECT observation_json->'venueEvent'->'facts'->>'exchangeTradeId'
FROM oms_private_capture_observations WHERE disposition=3
"""));
    }

    [Fact]
    public async Task FullScopeCurrencyAndOwnOrderLeg_AreIndependentPartitions()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        var store = new OmsPostgresPrivateEventCapture(db.Source);
        var first = Execution();
        var scopes = new[] { Scope(), Scope(tenant: "other"), Scope(environment: Domain.Oms.OmsEnvironment.Mainnet),
            Scope(account: "other"), Scope(sub: "desk-1"), Scope(sub: "desk-2") };
        foreach (var scope in scopes)
            Assert.Equal(OmsPrivateCaptureResult.Inserted, await store.CaptureAsync(first with { Scope = scope }));
        Assert.Equal(OmsPrivateCaptureResult.Inserted, await store.CaptureAsync(first with
        {
            Facts = first.Facts with { Currency = "ETH", NativeInstrument = "ETH-PERPETUAL" },
            Observation = first.Observation with { CaptureId = Guid.NewGuid() }
        }));
        Assert.Equal(OmsPrivateCaptureResult.Inserted, await store.CaptureAsync(first with
        {
            Facts = first.Facts with { ExchangeOrderId = "other-participant-order", Side = Domain.Enums.OrderSide.Sell },
            Observation = first.Observation with { CaptureId = Guid.NewGuid() }
        }));
        Assert.Equal(8L, await Count(db.Source, "oms_inbox"));
        Assert.Equal(8L, await Count(db.Source, "oms_private_capture_observations"));
        await AssertNoProjection(db.Source);
    }

    [Fact]
    public async Task ObservationInsertFailure_RollsBackBothNewFactAndCollisionIncident()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        await using var admin = await db.Source.OpenConnectionAsync();
        await OmsPostgresTestDatabase.Execute(admin, """
CREATE FUNCTION fixture_fail_capture() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN RAISE EXCEPTION 'fixture capture commit failure'; END; $$;
CREATE TRIGGER fixture_fail_capture BEFORE INSERT ON oms_private_capture_observations
FOR EACH ROW EXECUTE FUNCTION fixture_fail_capture();
""");
        var store = new OmsPostgresPrivateEventCapture(db.Source);
        var first = Execution();
        var error = await Assert.ThrowsAsync<PostgresException>(() => store.CaptureAsync(first));
        Assert.Equal("P0001", error.SqlState);
        Assert.Equal(0L, await Count(db.Source, "oms_inbox"));
        Assert.Equal(0L, await Count(db.Source, "oms_private_capture_observations"));
        await OmsPostgresTestDatabase.Execute(admin, "ALTER TABLE oms_private_capture_observations DISABLE TRIGGER fixture_fail_capture");
        Assert.Equal(OmsPrivateCaptureResult.Inserted, await store.CaptureAsync(first));
        await OmsPostgresTestDatabase.Execute(admin, "ALTER TABLE oms_private_capture_observations ENABLE TRIGGER fixture_fail_capture");
        var conflict = first with { Facts = first.Facts with { Price = 65001m },
            Observation = first.Observation with { CaptureId = Guid.NewGuid() } };
        await Assert.ThrowsAsync<PostgresException>(() => store.CaptureAsync(conflict));
        Assert.Equal(1L, await Count(db.Source, "oms_inbox"));
        Assert.Equal(1L, await Count(db.Source, "oms_private_capture_observations"));
        Assert.Equal(0L, await Count(db.Source, "oms_integrity_incidents"));
        // Only this case's added fault trigger is removed; all immutable guards remain enabled.
        await OmsPostgresTestDatabase.Execute(admin, "DROP TRIGGER fixture_fail_capture ON oms_private_capture_observations; DROP FUNCTION fixture_fail_capture()");
        Assert.Equal(OmsPrivateCaptureResult.Collision, await store.CaptureAsync(conflict));
        Assert.Equal(1L, await Count(db.Source, "oms_integrity_incidents"));
    }

    [Fact]
    public async Task FreshUpgradeAndDown_PreserveV1Bytes_RefuseDestructionOfV2Evidence()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(PostgresPreparation.FreshMigrations);
        var options = new DbContextOptionsBuilder<TradeOpsDbContext>().UseNpgsql(db.ConnectionString).Options;
        await using var context = new TradeOpsDbContext(options);
        await context.Database.MigrateAsync("20261025193000_AddOmsDurableJournal");
        var old = new OmsJournalInboxEvent(Scope(), "Deribit:trade-id", "trade-1",
            OmsJournalEventKind.Execution, null, "BTC-PERPETUAL", 1.000m, 100.00m, Time);
        Assert.Equal(OmsJournalInboxResult.Inserted, await new OmsPostgresJournal(db.Source).PersistInboxAsync(old));
        var bytes = await Scalar(db.Source, "SELECT event_json::text || encode(payload_sha256,'hex') FROM oms_inbox");
        await context.Database.MigrateAsync();
        Assert.Equal(bytes, await Scalar(db.Source, "SELECT event_json::text || encode(payload_sha256,'hex') FROM oms_inbox"));
        Assert.Equal(OmsPrivateCaptureResult.Inserted, await new OmsPostgresPrivateEventCapture(db.Source).CaptureAsync(Execution()));
        var refusal = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.MigrateAsync("20261025193000_AddOmsDurableJournal"));
        Assert.Equal("P0001", refusal.SqlState);
        Assert.Contains("Refusing destructive private capture rollback", refusal.MessageText);
        Assert.Equal(2L, await Count(db.Source, "oms_inbox"));
        Assert.Equal(1L, await Count(db.Source, "oms_private_capture_observations"));
        Assert.Equal(bytes, await Scalar(db.Source,
            "SELECT event_json::text || encode(payload_sha256,'hex') FROM oms_inbox WHERE schema_version=1"));
    }

    [Fact]
    public async Task LateOutOfOrderReports_AreRetainedWithoutApplyingLegacyStateOrInventingFills()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        var store = new OmsPostgresPrivateEventCapture(db.Source);
        var latest = Order() with { Facts = Order().Facts with
        { OrderState = "filled", FilledNativeAmount = 100m, LeavesNativeAmount = 0m, VenueTimestampUtc = Time.AddMinutes(2) } };
        var late = Order();
        Assert.Equal(OmsPrivateCaptureResult.Inserted, await store.CaptureAsync(latest));
        Assert.Equal(OmsPrivateCaptureResult.Inserted, await store.CaptureAsync(late));
        Assert.Equal(OmsPrivateCaptureResult.Duplicate, await store.CaptureAsync(late with
        { Observation = late.Observation with { CaptureId = Guid.NewGuid(), StreamEpoch = Guid.NewGuid() } }));
        Assert.Equal(2L, await Count(db.Source, "oms_inbox"));
        Assert.Equal(3L, await Count(db.Source, "oms_private_capture_observations"));
        await AssertNoProjection(db.Source);
    }

    [Fact]
    public async Task RestrictedApplicationRole_CapturesAndQuarantines_ImmutableAndTruncateGuardsRemain()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        await using var admin = await db.Source.OpenConnectionAsync();
        var role = "to_app_" + Guid.NewGuid().ToString("N");
        var password = Guid.NewGuid().ToString("N");
        await OmsPostgresTestDatabase.Execute(admin, $"""
CREATE ROLE "{role}" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD '{password}';
COMMENT ON ROLE "{role}" IS 'tradeops-tests:role:v1:{db.Name}';
GRANT CONNECT ON DATABASE "{db.Name}" TO "{role}";
GRANT USAGE ON SCHEMA public TO "{role}";
GRANT SELECT, INSERT, UPDATE ON oms_inbox, oms_private_capture_observations TO "{role}";
GRANT SELECT, INSERT ON oms_integrity_incidents TO "{role}";
GRANT USAGE ON SEQUENCE oms_integrity_incidents_incident_id_seq TO "{role}";
""");
        try
        {
            var cs = new NpgsqlConnectionStringBuilder(db.ConnectionString)
                { Username = role, Password = password, Pooling = true, MaxPoolSize = 20 };
            await using var application = NpgsqlDataSource.Create(cs.ConnectionString);
            await using var connection = await application.OpenConnectionAsync();
            Assert.Equal(1L, await Scalar(application, """
SELECT count(*) FROM pg_roles WHERE rolname=current_user
 AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication
"""));
            IOmsPrivateEventCapture store = new OmsPostgresPrivateEventCapture(application);
            var first = Execution();
            Assert.Equal(OmsPrivateCaptureResult.Inserted, await store.CaptureAsync(first));
            Assert.Equal(OmsPrivateCaptureResult.Duplicate, await store.CaptureAsync(first with
            { Observation = first.Observation with { CaptureId = Guid.NewGuid(), StreamEpoch = Guid.NewGuid() } }));
            Assert.Equal(OmsPrivateCaptureResult.Collision, await store.CaptureAsync(first with
            { Facts = first.Facts with { Fee = 0.01m }, Observation = first.Observation with { CaptureId = Guid.NewGuid() } }));
            foreach (var sql in new[] { "UPDATE oms_inbox SET event_json='{}'::jsonb",
                         "UPDATE oms_private_capture_observations SET observation_json='{}'::jsonb" })
            {
                var refused = await Assert.ThrowsAsync<PostgresException>(() => OmsPostgresTestDatabase.Execute(connection, sql));
                Assert.Equal("P0001", refused.SqlState);
            }
            var truncate = await Assert.ThrowsAsync<PostgresException>(() =>
                OmsPostgresTestDatabase.Execute(connection, "TRUNCATE oms_private_capture_observations"));
            Assert.Equal("42501", truncate.SqlState);
            var ownerTruncate = await Assert.ThrowsAsync<PostgresException>(() =>
                OmsPostgresTestDatabase.Execute(admin, "TRUNCATE oms_private_capture_observations"));
            Assert.Equal("P0001", ownerTruncate.SqlState);
            Assert.Equal(1L, await Count(application, "oms_inbox"));
            Assert.Equal(3L, await Count(application, "oms_private_capture_observations"));
            Assert.Equal(1L, await Count(application, "oms_integrity_incidents"));
        }
        finally
        {
            await OmsPostgresTestDatabase.Execute(admin, $"DROP OWNED BY \"{role}\"; DROP ROLE \"{role}\"");
        }
    }

    private static async Task AssertNoProjection(NpgsqlDataSource source)
    {
        foreach (var table in new[] { "\"Orders\"", "\"Fills\"", "oms_aggregates", "oms_commands", "oms_outbox" })
            Assert.Equal(0L, await Count(source, table));
    }

    private static Task<object?> Count(NpgsqlDataSource source, string table) => Scalar(source, $"SELECT count(*) FROM {table}");
    private static async Task<object?> Scalar(NpgsqlDataSource source, string sql)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }
}
