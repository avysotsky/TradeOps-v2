using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using TradeOps.Worker;
using Xunit;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;

namespace TradeOps.UnitTests;

public sealed class DeribitCaptureHostPostgresTests
{
    [Fact]
    public async Task Host_CommitsOnlyPrivateInbox_RefusesSecondOwnerBeforeSocket_AndReleasesOnStop()
    {
        await using var role = await Role.Create();
        var socket = new Socket();
        socket.OnSubscribe = (s, id, p) => { s.Emit(Trade()); s.Reply(id, p.GetProperty("channels")); };
        using var host = BuildHost(role.Connection, () => socket);
        var worker = Assert.IsType<DeribitReadOnlyCaptureWorker>(Assert.Single(host.Services.GetServices<IHostedService>()));
        await host.StartAsync();
        try
        {
            var timer = Stopwatch.StartNew();
            while (await Scalar(role.Source, "SELECT count(*) FROM oms_inbox") is not 1L)
            {
                if (worker.ExecuteTask!.IsCompleted) await worker.ExecuteTask;
                Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15), "Capture commit deadline exceeded.");
                await Task.Delay(20);
            }
            Assert.Equal(OmsPrivateStreamPhase.Recovering, worker.Status!.Phase);
            Assert.Equal(Scope(), worker.Status.Scope);
            Assert.Equal(1L, await Scalar(role.Source, "SELECT count(*) FROM oms_private_capture_observations"));
            Assert.Equal(0L, await Scalar(role.Source, "SELECT count(*) FROM oms_integrity_incidents"));
            var opened = 0;
            using var second = BuildHost(role.Connection, () => { Interlocked.Increment(ref opened); return new Socket(); });
            var rejected = Assert.IsType<DeribitReadOnlyCaptureWorker>(Assert.Single(second.Services.GetServices<IHostedService>()));
            await rejected.StartAsync(CancellationToken.None);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => rejected.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Null(error.InnerException);
            Assert.Equal(0, opened);
            foreach (var table in new[] { "\"Orders\"", "\"Fills\"", "oms_commands", "oms_outbox", "oms_aggregates" })
                Assert.Equal(0L, await Scalar(role.Database.Source, $"SELECT count(*) FROM {table}"));
        }
        finally { await host.StopAsync(); }
        Assert.Equal(OmsPrivateStreamPhase.Stopped, worker.Status!.Phase);
        await using var reacquired = await OmsPostgresPrivateCaptureOwner.AcquireAsync(role.Source, Scope());
    }

    [Fact]
    public async Task AdministrativeDatabaseRole_IsRejectedBeforeOpeningAnySocket()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        var opened = 0;
        using var host = BuildHost(db.ConnectionString, () => { Interlocked.Increment(ref opened); return new Socket(); });
        var worker = Assert.IsType<DeribitReadOnlyCaptureWorker>(Assert.Single(host.Services.GetServices<IHostedService>()));
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = host.Services.GetRequiredService<IHostApplicationLifetime>()
            .ApplicationStopping.Register(() => stopped.TrySetResult());
        try { await host.StartAsync(); }
        catch (OperationCanceledException) when (stopped.Task.IsCompleted) { }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(15)));
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Null(error.InnerException);
        Assert.Equal(0, opened);
        Assert.Null(worker.Status);
        Assert.Equal(0L, await Scalar(db.Source, "SELECT count(*) FROM oms_inbox"));
    }

    [Theory]
    [InlineData("missing-capture-grant")]
    [InlineData("legacy-write-grant")]
    public async Task IncorrectApplicationGrants_FailClosedBeforeCapture(string scenario)
    {
        await using var role = await Role.Create(scenario);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OmsPostgresPrivateCaptureOwner.AcquireAsync(role.Source, Scope()));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(role.Name, error.ToString());
        Assert.Equal(0L, await Scalar(role.Database.Source, "SELECT count(*) FROM oms_inbox"));
    }

    [Fact]
    public async Task LostOwnedLease_StopsOldCapture_AndDoesNotSilentlyReacquire()
    {
        await using var role = await Role.Create();
        await using var first = await OmsPostgresPrivateCaptureOwner.AcquireAsync(role.Source, Scope());
        await using (var other = await OmsPostgresPrivateCaptureOwner.AcquireAsync(role.Source, Scope("other")))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => first.CaptureAsync(OmsPrivateEventContractTests.Execution(Scope("other"))));
        }
        // Kill only this generated role's single owned advisory-lock backend in its generated test DB.
        await using (var admin = await role.Database.Source.OpenConnectionAsync())
        {
            await using var find = new NpgsqlCommand("""
SELECT a.pid FROM pg_stat_activity a WHERE a.datname=current_database() AND a.usename=@role
 AND EXISTS (SELECT 1 FROM pg_locks l WHERE l.pid=a.pid AND l.locktype='advisory' AND l.granted)
""", admin);
            find.Parameters.AddWithValue("role", role.Name);
            var pids = new List<int>();
            await using (var rows = await find.ExecuteReaderAsync()) while (await rows.ReadAsync()) pids.Add(rows.GetInt32(0));
            var pid = Assert.Single(pids);
            await using var terminate = new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", admin);
            terminate.Parameters.AddWithValue("pid", pid);
            Assert.Equal(true, await terminate.ExecuteScalarAsync());
        }
        var item = OmsPrivateEventContractTests.Execution(Scope());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => first.CaptureAsync(item));
        Assert.Null(error.InnerException);
        Assert.Equal(0L, await Scalar(role.Source, "SELECT count(*) FROM oms_inbox"));
        await using var next = await OmsPostgresPrivateCaptureOwner.AcquireAsync(role.Source, Scope());
        Assert.Equal(OmsPrivateCaptureResult.Inserted, await next.CaptureAsync(item));
        Assert.Equal(1L, await Scalar(role.Source, "SELECT count(*) FROM oms_inbox"));
    }

    private static IHost BuildHost(string connection, Func<IDeribitPrivateSocket> socket)
    {
        var builder = DeribitCaptureHostTests.Builder(DeribitCaptureHostTests.Configuration(connection));
        Assert.True(DeribitCaptureStartup.TryConfigure(builder));
        builder.Services.Replace(ServiceDescriptor.Singleton(Stream(socket)));
        return builder.Build();
    }

    private static async Task<object?> Scalar(NpgsqlDataSource source, string sql)
    {
        await using var db = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, db);
        return await command.ExecuteScalarAsync();
    }

    private sealed class Role : IAsyncDisposable
    {
        public OmsPostgresTestDatabase Database { get; }
        public string Name { get; } = "to_app_" + Guid.NewGuid().ToString("N");
        public string Connection { get; private set; } = "";
        public NpgsqlDataSource Source { get; private set; } = null!;
        private Role(OmsPostgresTestDatabase database) => Database = database;

        public static async Task<Role> Create(string? scenario = null)
        {
            var value = new Role(await OmsPostgresTestDatabase.CreateAsync());
            var password = Guid.NewGuid().ToString("N");
            await using var admin = await value.Database.Source.OpenConnectionAsync();
            try
            {
                await OmsPostgresTestDatabase.Execute(admin, $"""
CREATE ROLE "{value.Name}" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{password}';
COMMENT ON ROLE "{value.Name}" IS 'tradeops-tests:role:v1:{value.Database.Name}';
GRANT CONNECT ON DATABASE "{value.Database.Name}" TO "{value.Name}";
GRANT USAGE ON SCHEMA public TO "{value.Name}";
GRANT SELECT,INSERT,UPDATE ON oms_inbox,oms_private_capture_observations TO "{value.Name}";
GRANT SELECT,INSERT ON oms_integrity_incidents TO "{value.Name}";
GRANT USAGE ON SEQUENCE oms_integrity_incidents_incident_id_seq TO "{value.Name}";
""");
                if (scenario == "missing-capture-grant")
                    await OmsPostgresTestDatabase.Execute(admin, $"REVOKE UPDATE ON oms_private_capture_observations FROM \"{value.Name}\"");
                if (scenario == "legacy-write-grant")
                    await OmsPostgresTestDatabase.Execute(admin, $"GRANT INSERT ON \"Orders\" TO \"{value.Name}\"");
                value.Connection = new NpgsqlConnectionStringBuilder(value.Database.ConnectionString)
                    { Username = value.Name, Password = password, Pooling = true, MaxPoolSize = 8 }.ConnectionString;
                value.Source = NpgsqlDataSource.Create(value.Connection);
                return value;
            }
            catch
            {
                await OmsPostgresTestDatabase.Execute(admin, $"DROP OWNED BY \"{value.Name}\"; DROP ROLE IF EXISTS \"{value.Name}\"");
                await admin.DisposeAsync();
                await value.Database.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            await using (var admin = await Database.Source.OpenConnectionAsync())
                await OmsPostgresTestDatabase.Execute(admin, $"DROP OWNED BY \"{Name}\"; DROP ROLE \"{Name}\"");
            await Database.DisposeAsync();
        }
    }
}
