using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;
using Live = TradeOps.UnitTests.DeribitRecoveryPublicationPostgresTests.Live;
using Inputs = TradeOps.UnitTests.OmsRiskAdmissionPostgresTests.Inputs;
using FrozenClock = TradeOps.UnitTests.OmsRiskAdmissionPostgresTests.FrozenClock;

namespace TradeOps.UnitTests;

public sealed class OmsRiskControlsPostgresTests
{
    private static OmsJournalCommandRequest Command() => DeribitCommandReceiptFixtures.Command() with { Scope = Scope() };
    private static OmsRiskControlScopeV1 ControlScope(int level) => level switch {
        1 => OmsRiskControlScopeV1.Global, 2 => OmsRiskControlScopeV1.ForVenue(Scope()), _ => OmsRiskControlScopeV1.ForAccount(Scope()) };
    private static OmsRiskControlRequestV1 Change(OmsRiskControlScopeV1 scope, OmsRiskControlKind kind = OmsRiskControlKind.KillSwitch,
        long expected = 0, bool blocked = true) => new(scope, kind, Guid.NewGuid(), expected, blocked, "Test operator decision");
    private static async Task<long> Count(OmsPostgresTestDatabase db, string table)
    { await using var q = db.Source.CreateCommand($"SELECT count(*) FROM {table}"); return (long)(await q.ExecuteScalarAsync())!; }

    [Theory]
    [InlineData(1, 1)] [InlineData(2, 1)] [InlineData(3, 1)]
    [InlineData(1, 2)] [InlineData(2, 2)] [InlineData(3, 2)]
    public async Task AllLatchLevelsAndKindsPersistAndDenyNewAdmission(int level, int kind)
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); await using var role = await Operator.Open(db);
        var request = Change(ControlScope(level), (OmsRiskControlKind)kind); await role.Allow(request);
        var first = await role.Controls.SetAsync(request); Assert.Equal(role.Name, first.ActorRole); Assert.Equal(1, first.Revision);
        Assert.Equal(first, await new OmsPostgresRiskControls(role.Source).SetAsync(request));
        var clock = new FrozenClock(); await using var live = await Live.Open(db, clock);
        var (token, doc, qualified) = await live.Prepare(); var published = await live.Store.PublishAsync(token, doc, qualified);
        Assert.Equal(OmsRiskAdmissionResult.ControlBlocked, await new OmsPostgresRiskAdmission(db.Source, new Inputs(clock), clock).AdmitAsync(Command(), published, qualified));
        Assert.Equal(0, await Count(db, "oms_commands")); Assert.Equal(0, await Count(db, "oms_risk_reservations_v1"));
        Assert.Equal(1, await Count(db, "oms_risk_control_events_v1")); Assert.Equal(1, await Count(db, "oms_risk_admission_audit_v1"));
    }

    [Fact]
    public async Task ClearingKillDoesNotClearCircuitAndNeverRevivesOldReservationStamp()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); await using var role = await Operator.Open(db);
        var kill = Change(ControlScope(3)); var circuit = Change(ControlScope(3), OmsRiskControlKind.CircuitBreaker);
        await role.Allow(kill); await role.Allow(circuit);
        var clock = new FrozenClock(); await using var live = await Live.Open(db, clock);
        var (token, doc, qualified) = await live.Prepare(); var published = await live.Store.PublishAsync(token, doc, qualified);
        var risk = new OmsPostgresRiskAdmission(db.Source, new Inputs(clock), clock); var command = Command();
        Assert.Equal(OmsRiskAdmissionResult.Reserved, await risk.AdmitAsync(command, published, qualified));
        var reader = new OmsPostgresRiskControls(db.Source);
        Assert.True(await reader.IsReservationControlCurrentAsync(command.Scope, command.CommandId));
        await role.Controls.SetAsync(kill); await role.Controls.SetAsync(circuit);
        Assert.False(await reader.IsReservationControlCurrentAsync(command.Scope, command.CommandId));
        await role.Controls.SetAsync(kill with { DecisionId = Guid.NewGuid(), ExpectedRevision = 1, Blocked = false });
        Assert.Equal(OmsRiskAdmissionResult.ControlBlocked, await risk.AdmitAsync(Command(), published, qualified));
        var stale = await Assert.ThrowsAsync<PostgresException>(() => role.Controls.SetAsync(circuit with { DecisionId = Guid.NewGuid(), Blocked = false }));
        Assert.Equal("40001", stale.SqlState);
        await role.Controls.SetAsync(circuit with { DecisionId = Guid.NewGuid(), ExpectedRevision = 1, Blocked = false });
        Assert.False(await reader.IsReservationControlCurrentAsync(command.Scope, command.CommandId));
        Assert.Equal(OmsRiskAdmissionResult.Reserved, await risk.AdmitAsync(Command(), published, qualified));
        Assert.Equal(4, await Count(db, "oms_risk_control_events_v1"));
        await using var immutable = db.Source.CreateCommand("TRUNCATE oms_risk_control_events_v1");
        await Assert.ThrowsAsync<PostgresException>(() => immutable.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task ActualRoleAndExactPermissionAreRequiredAndActivateOnlyCannotClearOrTamper()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); await using var role = await Operator.Open(db);
        var request = Change(ControlScope(3));
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => role.Controls.SetAsync(request))).SqlState);
        await role.Allow(request, clear: false); await role.Controls.SetAsync(request);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => role.Controls.SetAsync(request with {
            DecisionId = Guid.NewGuid(), ExpectedRevision = 1, Blocked = false }))).SqlState);
        await Assert.ThrowsAsync<PostgresException>(() => role.Controls.SetAsync(request with { Reason = "different decision" }));
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => role.Controls.SetAsync(Change(ControlScope(1))))).SqlState);
        foreach (var sql in new[] { "DELETE FROM oms_risk_control_events_v1", "TRUNCATE oms_risk_control_events_v1",
            "UPDATE oms_risk_control_events_v1 SET blocked=false", "INSERT INTO oms_risk_control_permissions_v1 SELECT * FROM oms_risk_control_permissions_v1" })
        {
            await using var q = role.Source.CreateCommand(sql);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => q.ExecuteNonQueryAsync())).SqlState);
        }
    }

    [Theory]
    [InlineData("account")] [InlineData("subaccount")] [InlineData("tenant")]
    [InlineData("venue")] [InlineData("environment")] [InlineData("venue-tenant")]
    public async Task LatchNeverLeaksAcrossScope(string difference)
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); await using var role = await Operator.Open(db);
        var scope = difference switch {
            "account" => ControlScope(3) with { Account = "another" },
            "subaccount" => ControlScope(3) with { Subaccount = "another" },
            "tenant" => ControlScope(3) with { Tenant = "another" },
            "venue" => ControlScope(2) with { Venue = "ANOTHER" },
            "environment" => ControlScope(2) with { Environment = 4 },
            _ => ControlScope(2) with { Tenant = "another" }
        };
        Assert.NotEqual(scope, scope.Level == OmsRiskControlLevel.Venue ? ControlScope(2) : ControlScope(3));
        var request = Change(scope); await role.Allow(request); await role.Controls.SetAsync(request);
        var clock = new FrozenClock(); await using var live = await Live.Open(db, clock);
        var (token, doc, qualified) = await live.Prepare(); var publication = await live.Store.PublishAsync(token, doc, qualified);
        Assert.Equal(OmsRiskAdmissionResult.Reserved, await new OmsPostgresRiskAdmission(db.Source, new Inputs(clock), clock).AdmitAsync(Command(), publication, qualified));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task AdmissionAndKillHaveAnActualCommitBoundary(bool admissionFirst)
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); await using var role = await Operator.Open(db);
        var request = Change(ControlScope(1)); await role.Allow(request);
        var clock = new FrozenClock(); await using var live = await Live.Open(db, clock);
        var (token, doc, qualified) = await live.Prepare(); var publication = await live.Store.PublishAsync(token, doc, qualified);
        var command = Command();
        if (admissionFirst)
        {
            var blockedInputs = new PausedInputs(new Inputs(clock));
            var risk = new OmsPostgresRiskAdmission(db.Source, blockedInputs, clock);
            var admission = risk.AdmitAsync(command, publication, qualified);
            await blockedInputs.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var kill = role.Controls.SetAsync(request);
            try { await AwaitAdvisoryWait(db); Assert.False(kill.IsCompleted); }
            finally { blockedInputs.Release.TrySetResult(); }
            Assert.Equal(OmsRiskAdmissionResult.Reserved, await admission); await kill;
        }
        else
        {
            await using var connection = await role.Source.OpenConnectionAsync(); await using var tx = await connection.BeginTransactionAsync();
            await using (var q = ControlCommand(connection, tx, request)) await q.ExecuteNonQueryAsync();
            var admission = new OmsPostgresRiskAdmission(db.Source, new Inputs(clock), clock).AdmitAsync(command, publication, qualified);
            await AwaitAdvisoryWait(db); Assert.False(admission.IsCompleted);
            await tx.CommitAsync(); Assert.Equal(OmsRiskAdmissionResult.ControlBlocked, await admission);
        }
        Assert.Equal(admissionFirst ? 1 : 0, await Count(db, "oms_risk_reservations_v1"));
        Assert.Equal(OmsRiskAdmissionResult.ControlBlocked, await new OmsPostgresRiskAdmission(db.Source, new Inputs(clock), clock).AdmitAsync(Command(), publication, qualified));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task FailedAuditCannotReturnApprovalOrSilentlyDropDenial(bool eligible)
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var clock = new FrozenClock(); await using var live = await Live.Open(db, clock);
        var (token, doc, qualified) = await live.Prepare(); var publication = await live.Store.PublishAsync(token, doc, qualified);
        await using (var q = db.Source.CreateCommand("""
CREATE FUNCTION test_fail_admission_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'synthetic audit failure'; END $$;
CREATE TRIGGER test_fail_admission_audit BEFORE INSERT ON oms_risk_admission_audit_v1 FOR EACH ROW EXECUTE FUNCTION test_fail_admission_audit();
""")) await q.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<PostgresException>(() => new OmsPostgresRiskAdmission(db.Source, new Inputs(clock) { Missing = !eligible }, clock).AdmitAsync(Command(), publication, qualified));
        Assert.Equal(0, await Count(db, "oms_commands")); Assert.Equal(0, await Count(db, "oms_risk_reservations_v1"));
        Assert.Equal(0, await Count(db, "oms_risk_admission_controls_v1")); Assert.True(await live.Store.IsPublishedCurrentAsync(publication));
    }

    [Fact]
    public async Task StaleOrderCasRollsBackTentativeAggregateButRetainsImmutableDenial()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var clock = new FrozenClock(); await using var live = await Live.Open(db, clock);
        var (token, doc, qualified) = await live.Prepare(); var publication = await live.Store.PublishAsync(token, doc, qualified);
        Assert.Equal(OmsRiskAdmissionResult.StaleOrderVersion, await new OmsPostgresRiskAdmission(db.Source, new Inputs(clock), clock).AdmitAsync(Command() with { ExpectedAggregateVersion = new(1) }, publication, qualified));
        Assert.Equal(0, await Count(db, "oms_aggregates")); Assert.Equal(0, await Count(db, "oms_commands"));
        Assert.Equal(1, await Count(db, "oms_risk_admission_audit_v1"));
        await using var q = db.Source.CreateCommand("DELETE FROM oms_risk_admission_audit_v1");
        await Assert.ThrowsAsync<PostgresException>(() => q.ExecuteNonQueryAsync());
    }

    private sealed class PausedInputs(IOmsRiskInputsReader inner) : IOmsRiskInputsReader
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<OmsRiskInputsV1?> ReadAsync(OmsJournalCommandRequest command, OmsRecoveredAccountStateV1 baseline, CancellationToken ct = default)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), ct); return await inner.ReadAsync(command, baseline, ct); }
    }

    private static async Task AwaitAdvisoryWait(OmsPostgresTestDatabase db)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            await using var q = db.Source.CreateCommand("SELECT EXISTS(SELECT 1 FROM pg_locks WHERE locktype='advisory' AND NOT granted AND database=(SELECT oid FROM pg_database WHERE datname=current_database()))");
            if (await q.ExecuteScalarAsync(deadline.Token) is true) return;
            await Task.Delay(10, deadline.Token);
        }
    }

    private static NpgsqlCommand ControlCommand(NpgsqlConnection db, NpgsqlTransaction tx, OmsRiskControlRequestV1 r)
    {
        var q = new NpgsqlCommand("SELECT * FROM public.oms_set_risk_control_v1(@l,@t,@v,@e,@a,@s,@k,@d,@x,@b,@n)", db, tx);
        q.Parameters.AddWithValue("l", (short)r.Scope.Level); q.Parameters.AddWithValue("t", r.Scope.Tenant);
        q.Parameters.AddWithValue("v", r.Scope.Venue); q.Parameters.AddWithValue("e", r.Scope.Environment);
        q.Parameters.AddWithValue("a", r.Scope.Account); q.Parameters.AddWithValue("s", r.Scope.Subaccount);
        q.Parameters.AddWithValue("k", (short)r.Kind); q.Parameters.AddWithValue("d", r.DecisionId);
        q.Parameters.AddWithValue("x", r.ExpectedRevision); q.Parameters.AddWithValue("b", r.Blocked); q.Parameters.AddWithValue("n", r.Reason); return q;
    }

    private sealed class Operator(OmsPostgresTestDatabase db) : IAsyncDisposable
    {
        internal readonly string Name = "to_risk_" + Guid.NewGuid().ToString("N");
        internal NpgsqlDataSource Source = null!;
        internal OmsPostgresRiskControls Controls = null!;
        private const string Signature = "public.oms_set_risk_control_v1(smallint,text,text,smallint,text,text,smallint,uuid,bigint,boolean,text)";
        internal static async Task<Operator> Open(OmsPostgresTestDatabase db)
        {
            var role = new Operator(db);
            await using (var q = db.Source.CreateCommand($"""
CREATE ROLE "{role.Name}" NOLOGIN;
COMMENT ON ROLE "{role.Name}" IS 'tradeops-tests:role:v1:{db.Name}';
GRANT USAGE ON SCHEMA public TO "{role.Name}";
GRANT EXECUTE ON FUNCTION {Signature} TO "{role.Name}";
""")) await q.ExecuteNonQueryAsync();
            role.Source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(db.ConnectionString) { Pooling = false, Options = "-c role=" + role.Name }.ConnectionString);
            role.Controls = new(role.Source); return role;
        }
        internal async Task Allow(OmsRiskControlRequestV1 r, bool clear = true)
        {
            await using var q = db.Source.CreateCommand("""
INSERT INTO oms_risk_control_permissions_v1 VALUES(@l,@t,@v,@e,@a,@s,@k,@role,true,@clear)
""");
            q.Parameters.AddWithValue("l", (short)r.Scope.Level); q.Parameters.AddWithValue("t", r.Scope.Tenant);
            q.Parameters.AddWithValue("v", r.Scope.Venue); q.Parameters.AddWithValue("e", r.Scope.Environment);
            q.Parameters.AddWithValue("a", r.Scope.Account); q.Parameters.AddWithValue("s", r.Scope.Subaccount);
            q.Parameters.AddWithValue("k", (short)r.Kind); q.Parameters.AddWithValue("role", Name); q.Parameters.AddWithValue("clear", clear);
            await q.ExecuteNonQueryAsync();
        }
        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            await using var q = db.Source.CreateCommand($"REVOKE USAGE ON SCHEMA public FROM \"{Name}\"; REVOKE EXECUTE ON FUNCTION {Signature} FROM \"{Name}\"; DROP ROLE \"{Name}\"");
            await q.ExecuteNonQueryAsync();
        }
    }
}
