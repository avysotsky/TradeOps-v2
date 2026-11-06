using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;
using Live = TradeOps.UnitTests.DeribitRecoveryPublicationPostgresTests.Live;

namespace TradeOps.UnitTests;

public sealed class OmsRiskAdmissionPostgresTests
{
    internal sealed class FrozenClock : TimeProvider
    {
        private readonly DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
    }
    internal sealed class Inputs(TimeProvider? clock = null) : IOmsRiskInputsReader
    {
        internal bool Missing;
        internal Func<OmsRiskInputsV1, OmsRiskInputsV1>? Transform;
        public Task<OmsRiskInputsV1?> ReadAsync(OmsJournalCommandRequest command, OmsRecoveredAccountStateV1 baseline, CancellationToken ct = default)
        {
            var now = (clock ?? TimeProvider.System).GetUtcNow();
            var value = new OmsRiskInputsV1(new(1, command.Scope, "test-policy-v1", 2, 2, 10, 2, 10, 10, 5, 2, 2),
                new(1, command.Scope, Convert.ToHexString(OmsRecoveryRunCodec.Encode(baseline).Sha256),
                    Convert.ToHexString(OmsJournalCanonical.EncodeCommand(command).Sha256), now, now.AddSeconds(5),
                    true, true, true, false, command.Payload.Quantity, command.Payload.Quantity, 0, 0, 100, 0, 1, 0));
            return Task.FromResult<OmsRiskInputsV1?>(Missing ? null : Transform?.Invoke(value) ?? value);
        }
    }
    private static OmsJournalCommandRequest Command() => DeribitCommandReceiptFixtures.Command() with { Scope = Scope() };
    private static async Task<long> Count(OmsPostgresTestDatabase db, string table)
    {
        await using var q = db.Source.CreateCommand($"SELECT count(*) FROM {table}"); return (long)(await q.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task ParallelAdmissionsCannotOversubscribeAndOwnVersionTransitionsRemainUsable()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var clock = new FrozenClock();
        await using var live = await Live.Open(db, clock);
        var (token, doc, qualified) = await live.Prepare(); var publication = await live.Store.PublishAsync(token, doc, qualified);
        var risk = new OmsPostgresRiskAdmission(db.Source, new Inputs(clock), clock);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => risk.AdmitAsync(Command(), publication, qualified)));
        Assert.Equal(2, outcomes.Count(x => x == OmsRiskAdmissionResult.Reserved));
        Assert.Equal(10, outcomes.Count(x => x == OmsRiskAdmissionResult.Denied));
        Assert.Equal(2, await Count(db, "oms_risk_reservations_v1")); Assert.Equal(2, await Count(db, "oms_commands"));
        Assert.Equal(2, await Count(db, "oms_outbox"));
    }

    [Fact]
    public async Task RetryAfterLostResultAndNewStoreInstanceNeverReservesTwiceOrAcceptsCollision()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); await using var live = await Live.Open(db);
        var (token, doc, qualified) = await live.Prepare(); var publication = await live.Store.PublishAsync(token, doc, qualified);
        var command = Command();
        Assert.Equal(OmsRiskAdmissionResult.Reserved, await new OmsPostgresRiskAdmission(db.Source, new Inputs()).AdmitAsync(command, publication, qualified));
        var restartedStore = new OmsPostgresRiskAdmission(db.Source, new Inputs { Missing = true });
        Assert.Equal(OmsRiskAdmissionResult.Duplicate, await restartedStore.AdmitAsync(command, publication, qualified));
        Assert.Equal(OmsRiskAdmissionResult.Collision, await restartedStore.AdmitAsync(command with { Payload = command.Payload with { Quantity = 2 } }, publication, qualified));
        Assert.Equal(1, await Count(db, "oms_risk_reservations_v1"));
    }

    [Theory]
    [InlineData("journal")] [InlineData("ingress")] [InlineData("generation")]
    public async Task ChangedBaselineCannotAcquireReservation(string change)
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); await using var live = await Live.Open(db);
        var (token, doc, qualified) = await live.Prepare(); var publication = await live.Store.PublishAsync(token, doc, qualified);
        if (change == "journal") await new OmsPostgresJournal(db.Source).PersistAsync(Command());
        if (change == "generation") await live.Store.BeginAsync(token.Start with { RunId = Guid.NewGuid() });
        if (change == "ingress") await live.Coordinator.RegisterReceiptAsync(live.Session, token.Start.Epoch, OmsPrivateCaptureReplayTests.Event(token.Start.Epoch));
        Assert.Equal(OmsRiskAdmissionResult.StaleBaseline, await new OmsPostgresRiskAdmission(db.Source, new Inputs()).AdmitAsync(Command(), publication, qualified));
        Assert.Equal(0, await Count(db, "oms_risk_reservations_v1"));
    }

    [Fact]
    public async Task ReservationFailureRollsBackCommandOutboxAndEvidenceVersion()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); await using var live = await Live.Open(db);
        var (token, doc, qualified) = await live.Prepare(); var publication = await live.Store.PublishAsync(token, doc, qualified);
        await using (var q = db.Source.CreateCommand("""
CREATE FUNCTION fail_test_risk() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'synthetic failure'; END $$;
CREATE TRIGGER fail_test_risk BEFORE INSERT ON oms_risk_reservations_v1 FOR EACH ROW EXECUTE FUNCTION fail_test_risk();
""")) await q.ExecuteNonQueryAsync();
        var risk = new OmsPostgresRiskAdmission(db.Source, new Inputs()); var command = Command();
        await Assert.ThrowsAsync<PostgresException>(() => risk.AdmitAsync(command, publication, qualified));
        Assert.Equal(0, await Count(db, "oms_commands")); Assert.Equal(0, await Count(db, "oms_outbox"));
        Assert.True(await live.Store.IsPublishedCurrentAsync(publication));
        await using (var q = db.Source.CreateCommand("DROP TRIGGER fail_test_risk ON oms_risk_reservations_v1")) await q.ExecuteNonQueryAsync();
        Assert.Equal(OmsRiskAdmissionResult.Reserved, await risk.AdmitAsync(command, publication, qualified));
        await using var immutable = db.Source.CreateCommand("DELETE FROM oms_risk_reservations_v1");
        await Assert.ThrowsAsync<PostgresException>(() => immutable.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task MissingTrustedPolicyOrValuationDeniesWithoutCreatingCommand()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); await using var live = await Live.Open(db);
        var (token, doc, qualified) = await live.Prepare(); var publication = await live.Store.PublishAsync(token, doc, qualified);
        Assert.Equal(OmsRiskAdmissionResult.Denied, await new OmsPostgresRiskAdmission(db.Source, new Inputs { Missing = true }).AdmitAsync(Command(), publication, qualified));
        Assert.Equal(0, await Count(db, "oms_commands"));
    }

    [Fact]
    public async Task NewRecoveryCannotForgetPendingReservations()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); await using var live = await Live.Open(db);
        var (token, doc, qualified) = await live.Prepare(); var publication = await live.Store.PublishAsync(token, doc, qualified);
        var risk = new OmsPostgresRiskAdmission(db.Source, new Inputs());
        Assert.Equal(OmsRiskAdmissionResult.Reserved, await risk.AdmitAsync(Command(), publication, qualified));
        Assert.Equal(OmsRiskAdmissionResult.Reserved, await risk.AdmitAsync(Command(), publication, qualified));
        var (nextToken, nextDoc, nextQualified) = await live.Prepare();
        var nextPublication = await live.Store.PublishAsync(nextToken, nextDoc, nextQualified);
        Assert.Equal(OmsRiskAdmissionResult.Denied, await new OmsPostgresRiskAdmission(db.Source, new Inputs()).AdmitAsync(Command(), nextPublication, nextQualified));
        Assert.Equal(2, await Count(db, "oms_risk_reservations_v1"));
    }

    [Theory]
    [InlineData(OmsJournalDispatchOutcome.Acknowledged)] [InlineData(OmsJournalDispatchOutcome.Ambiguous)]
    public async Task TransportOutcomeNeverReleasesExposure(OmsJournalDispatchOutcome outcome)
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); await using var live = await Live.Open(db);
        var (token, doc, qualified) = await live.Prepare(); var publication = await live.Store.PublishAsync(token, doc, qualified);
        var command = Command(); var risk = new OmsPostgresRiskAdmission(db.Source, new Inputs());
        Assert.Equal(OmsRiskAdmissionResult.Reserved, await risk.AdmitAsync(command, publication, qualified));
        var journal = new OmsPostgresJournal(db.Source);
        await journal.MarkDispatchStartedAsync(command.Scope, command.CommandId);
        await journal.RecordOutcomeAsync(command.Scope, command.CommandId, outcome);
        Assert.Equal(1, await Count(db, "oms_risk_reservations_v1"));
        Assert.Equal(OmsRiskAdmissionResult.StaleBaseline, await risk.AdmitAsync(Command(), publication, qualified));
    }
}
