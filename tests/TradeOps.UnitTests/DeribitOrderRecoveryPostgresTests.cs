using System.Text.Json;
using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;
using static TradeOps.UnitTests.DeribitOrderRecoveryProjectorTests;

namespace TradeOps.UnitTests;

public sealed class DeribitOrderRecoveryPostgresTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task FreshConnection_ReplaysCommittedObservationsAndOverlapWithoutLegacyWrites()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(pooling: false);
        var capture = new OmsPostgresPrivateEventCapture(db.Source);
        var fill = Fill();
        Assert.Equal(OmsPrivateCaptureResult.Inserted, await capture.CaptureAsync(Prior()));
        Assert.Equal(OmsPrivateCaptureResult.Inserted, await capture.CaptureAsync(fill));
        Assert.Equal(OmsPrivateCaptureResult.Duplicate, await capture.CaptureAsync(fill with
        { Observation = fill.Observation with { CaptureId = Guid.NewGuid(), Channel = "ws/trades",
            Source = OmsPrivateCaptureSource.WebSocket } }));
        Assert.Equal(OmsPrivateCaptureResult.Inserted, await capture.CaptureAsync(Report(filled: 4)));

        // Independent physical connection simulates rebuilding only from committed durable data.
        var facts = await Read(db.ConnectionString);
        var first = Project(facts.Where(x => x.Observation.StreamEpoch == Epoch).ToArray(),
            facts.Where(x => x.Observation.StreamEpoch != Epoch).ToArray());
        var replay = await Read(db.ConnectionString);
        var second = Project(replay.Where(x => x.Observation.StreamEpoch == Epoch).Reverse().ToArray(),
            replay.Where(x => x.Observation.StreamEpoch != Epoch).ToArray());
        Assert.True(first.IsConsistent);
        Assert.Equal(first.Orders, second.Orders);
        Assert.Equal(4, Assert.Single(first.Orders).KnownFilledNativeAmount);
        Assert.Equal(1, first.DistinctExecutions);
        await AssertCounts(db.ConnectionString, 3, 4, 0);
    }

    [Fact]
    public async Task QuarantinedAlternative_IsNotLostWhenRebuildingFromDurableObservations()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(pooling: false);
        var capture = new OmsPostgresPrivateEventCapture(db.Source);
        var fill = Fill();
        await capture.CaptureAsync(Prior());
        await capture.CaptureAsync(fill);
        Assert.Equal(OmsPrivateCaptureResult.Collision, await capture.CaptureAsync(fill with
        { Facts = fill.Facts with { Price = 2001 }, Observation = fill.Observation with { CaptureId = Guid.NewGuid() } }));
        await capture.CaptureAsync(Report(filled: 4));
        var facts = await Read(db.ConnectionString);
        var result = Project(facts.Where(x => x.Observation.StreamEpoch == Epoch).ToArray(),
            facts.Where(x => x.Observation.StreamEpoch != Epoch).ToArray());
        Assert.False(result.IsConsistent);
        Assert.Contains(result.Issues, x => x.Problem == DeribitOrderRecoveryProblem.ExecutionCollision);
        Assert.Equal(0, result.DistinctExecutions);
        await AssertCounts(db.ConnectionString, 3, 4, 1);
    }

    private static async Task<OmsPrivateEventEnvelope[]> Read(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        // Test-only decoder. Production read-side scope, hash/version checks and checkpoints
        // are a separate adapter; do not register this unfiltered fixture query in a host.
        await using var command = new NpgsqlCommand(
            "SELECT observation_json::text FROM oms_private_capture_observations ORDER BY capture_id", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<OmsPrivateEventEnvelope>();
        while (await reader.ReadAsync())
        {
            using var json = JsonDocument.Parse(reader.GetString(0));
            var root = json.RootElement;
            Assert.Equal(1, root.GetProperty("captureSchemaVersion").GetInt32());
            var venue = root.GetProperty("venueEvent");
            Assert.Equal(2, venue.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(Account.Account.Value, venue.GetProperty("account").GetString());
            result.Add(new(Account, venue.GetProperty("facts").Deserialize<OmsPrivateVenueFacts>(Json)!,
                root.GetProperty("observation").Deserialize<OmsPrivateObservation>(Json)!));
        }
        return result.ToArray();
    }

    private static async Task AssertCounts(string connectionString, long inbox, long observations, long incidents)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
SELECT (SELECT count(*) FROM oms_inbox), (SELECT count(*) FROM oms_private_capture_observations),
 (SELECT count(*) FROM oms_integrity_incidents), (SELECT count(*) FROM "Orders"), (SELECT count(*) FROM "Fills")
""", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(inbox, reader.GetInt64(0));
        Assert.Equal(observations, reader.GetInt64(1));
        Assert.Equal(incidents, reader.GetInt64(2));
        Assert.Equal(0, reader.GetInt64(3));
        Assert.Equal(0, reader.GetInt64(4));
    }
}
