using System.Net.WebSockets;
using System.Threading.Channels;
using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;

namespace TradeOps.UnitTests;

public sealed class DeribitPrivateStreamPostgresTests
{
    [Fact]
    public async Task StreamPipeline_ReconnectCommitsWithRestrictedRole_StableDedupAndNoLegacyWrites()
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
            var capture = new CommittedCapture(new OmsPostgresPrivateEventCapture(application));
            await using var coordinator = new OmsPrivateCaptureCoordinator(capture);
            var session = await coordinator.SwitchAsync(Scope());
            var first = new Socket(); var second = new Socket();
            var sockets = new Queue<Socket>(new[] { first, second });
            foreach (var socket in sockets) socket.OnSubscribe = (s, id, p) => { s.Emit(Trade()); s.Reply(id, p.GetProperty("channels")); };
            using var stop = new CancellationTokenSource();
            var run = Stream(() => sockets.Dequeue()).RunCaptureAsync(coordinator, session, Plan(), stop.Token);
            try
            {
                var a = await capture.Next();
                Assert.Equal(OmsPrivateCaptureResult.Inserted, a.Result);
                // Receiving the next item means the underlying PostgreSQL CaptureAsync COMMIT returned.
                Assert.Equal(1L, await Scalar(application, "SELECT count(*) FROM oms_inbox"));
                first.Fail(new WebSocketException("synthetic disconnect"));
                var b = await capture.Next();
                Assert.Equal(OmsPrivateCaptureResult.Duplicate, b.Result);
                Assert.NotEqual(a.Item.Observation.StreamEpoch, b.Item.Observation.StreamEpoch);
                Assert.Equal(1L, await Scalar(application, "SELECT count(*) FROM oms_inbox"));
                Assert.Equal(2L, await Scalar(application, "SELECT count(*) FROM oms_private_capture_observations"));
                Assert.Equal(0L, await Scalar(application, "SELECT count(*) FROM oms_integrity_incidents"));
                Assert.Equal(OmsPrivateStreamPhase.Recovering, coordinator.Status!.Phase);
                Assert.Equal(1L, await Scalar(application, """
SELECT count(*) FROM pg_roles WHERE rolname=current_user
 AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication
"""));
                foreach (var table in new[] { "\"Orders\"", "\"Fills\"", "oms_commands", "oms_outbox", "oms_aggregates" })
                    Assert.Equal(0L, await Scalar(db.Source, $"SELECT count(*) FROM {table}"));
            }
            finally { stop.Cancel(); await run.WaitAsync(Deadline); }
        }
        finally { await OmsPostgresTestDatabase.Execute(admin, $"DROP OWNED BY \"{role}\"; DROP ROLE \"{role}\""); }
    }

    private static async Task<object?> Scalar(NpgsqlDataSource source, string sql)
    {
        await using var db = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, db);
        return await command.ExecuteScalarAsync();
    }
    private sealed class CommittedCapture(IOmsPrivateEventCapture inner) : IOmsPrivateEventCapture
    {
        private readonly Channel<(OmsPrivateEventEnvelope Item, OmsPrivateCaptureResult Result)> committed =
            Channel.CreateUnbounded<(OmsPrivateEventEnvelope, OmsPrivateCaptureResult)>();
        public async Task<OmsPrivateCaptureResult> CaptureAsync(OmsPrivateEventEnvelope envelope, CancellationToken ct = default)
        {
            var result = await inner.CaptureAsync(envelope, ct);
            committed.Writer.TryWrite((envelope, result));
            return result;
        }
        public Task<(OmsPrivateEventEnvelope Item, OmsPrivateCaptureResult Result)> Next() =>
            committed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
    }
}
