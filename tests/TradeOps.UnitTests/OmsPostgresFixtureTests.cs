using Microsoft.EntityFrameworkCore;
using Npgsql;
using TradeOps.Infrastructure.Persistence;
using TradeOps.UnitTests.Support;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class OmsPostgresFixtureTests
{
    [Fact]
    public async Task TemplateClones_CommitAcrossIndependentConnections_ThenReleasePoolsAndRemainEmpty()
    {
        string name, template;
        await using (var first = await OmsPostgresTestDatabase.CreateAsync())
        {
            name = first.Name;
            template = Assert.IsType<string>(first.SourceTemplate);
            await using var one = await first.Source.OpenConnectionAsync();
            await using var two = await first.Source.OpenConnectionAsync();
            Assert.NotEqual(one.ProcessID, two.ProcessID);
            await using (var transaction = await one.BeginTransactionAsync())
            {
                await OmsPostgresTestDatabase.Execute(one,
                    "CREATE TABLE fixture_isolation_probe(id integer); INSERT INTO fixture_isolation_probe VALUES (42)");
                await transaction.CommitAsync();
            }
            Assert.Equal(42, await OmsPostgresTestDatabase.Scalar(two, "SELECT id FROM fixture_isolation_probe"));
            Assert.Equal(false, await OmsPostgresTestDatabase.Scalar(one,
                $"SELECT datallowconn FROM pg_database WHERE datname='{template}'"));
            Assert.Equal(0L, await OmsPostgresTestDatabase.Scalar(one, """
SELECT count(*) FROM pg_database d, LATERAL aclexplode(d.datacl) a
WHERE datname=current_database() AND a.grantee=0 AND a.privilege_type='CONNECT'
"""));
        }
        await using var second = await OmsPostgresTestDatabase.CreateAsync();
        Assert.NotEqual(name, second.Name);
        Assert.Equal(template, second.SourceTemplate);
        await using var connection = await second.Source.OpenConnectionAsync();
        Assert.Equal(DBNull.Value, await OmsPostgresTestDatabase.Scalar(connection,
            "SELECT to_regclass('fixture_isolation_probe')::text"));
        Assert.Equal(0L, await OmsPostgresTestDatabase.Scalar(connection, "SELECT count(*) FROM oms_commands"));
        Assert.Equal(0L, await OmsPostgresTestDatabase.Scalar(connection,
            $"SELECT count(*) FROM pg_database WHERE datname='{name}'"));
    }

    [Fact]
    public async Task FreshMode_AppliesTheActualMigrationChainWithoutTemplate()
    {
        await using var database = await OmsPostgresTestDatabase.CreateAsync(PostgresPreparation.FreshMigrations);
        Assert.Null(database.SourceTemplate);
        var options = new DbContextOptionsBuilder<TradeOpsDbContext>().UseNpgsql(database.ConnectionString).Options;
        await using var context = new TradeOpsDbContext(options);
        Assert.Equal(context.Database.GetMigrations(), await context.Database.GetAppliedMigrationsAsync());
        await using var connection = await database.Source.OpenConnectionAsync();
        Assert.NotNull(await OmsPostgresTestDatabase.Scalar(connection, "SELECT 'oms_commands'::regclass::text"));
    }

    [Fact]
    public async Task MissingOrWrongServerIdentity_RefusesBeforeDatabaseMutation()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OmsPostgresTestDatabase.CreateForConnectionAsync(null));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OmsPostgresTestDatabase.CreateForConnectionAsync("Host=127.0.0.1;Database=tradeops_dev;Username=postgres"));
        await using var database = await OmsPostgresTestDatabase.CreateAsync();
        // Same host/credentials but a clone is NOT the marked server admin database.
        await using var connection = await database.Source.OpenConnectionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => OmsPostgresTestDatabase.ValidateServerAsync(connection));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OmsPostgresTestDatabase.CreateForConnectionAsync(database.ConnectionString));
        Assert.Equal(0L, await OmsPostgresTestDatabase.Scalar(connection, "SELECT count(*) FROM oms_commands"));
    }

    [Fact]
    public async Task CrashRemnants_CleanOnlyMarkedIdleDatabases_RefuseUnmarkedOrActive()
    {
        // The only direct admin mutations here are uniquely generated sentinels created by this case.
        var builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TRADEOPS_B2_POSTGRES"))
            { Pooling = false };
        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        await OmsPostgresTestDatabase.ValidateServerAsync(admin);
        var name = "to_test_" + Guid.NewGuid().ToString("N");
        var marker = "tradeops-tests:database:v1:" + name;
        var oldHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var oldTemplate = "to_tpl_" + oldHash[..24];
        await OmsPostgresTestDatabase.Execute(admin, $"CREATE DATABASE \"{name}\" TEMPLATE template0");
        try
        {
            await OmsPostgresTestDatabase.Execute(admin, $"COMMENT ON DATABASE \"{name}\" IS 'fixture-refusal-sentinel'");
            await Assert.ThrowsAsync<InvalidOperationException>(() => OmsPostgresTestDatabase.CreateAsync());
            Assert.Equal(1L, await OmsPostgresTestDatabase.Scalar(admin,
                $"SELECT count(*) FROM pg_database WHERE datname='{name}'"));
            await OmsPostgresTestDatabase.Execute(admin, $"COMMENT ON DATABASE \"{name}\" IS '{marker}'");
            builder.Database = name;
            await using (var active = new NpgsqlConnection(builder.ConnectionString))
            {
                await active.OpenAsync();
                await Assert.ThrowsAsync<InvalidOperationException>(() => OmsPostgresTestDatabase.CreateAsync());
                Assert.Equal(1, await OmsPostgresTestDatabase.Scalar(active, "SELECT 1"));
            }
            await OmsPostgresTestDatabase.Execute(admin, $"CREATE DATABASE \"{oldTemplate}\" TEMPLATE template0");
            await OmsPostgresTestDatabase.Execute(admin,
                $"COMMENT ON DATABASE \"{oldTemplate}\" IS 'tradeops-tests:template:v1:{oldHash}'; " +
                $"ALTER DATABASE \"{oldTemplate}\" ALLOW_CONNECTIONS false");
            await using (var recovered = await OmsPostgresTestDatabase.CreateAsync())
            {
                Assert.Equal(0L, await OmsPostgresTestDatabase.Scalar(admin,
                    $"SELECT count(*) FROM pg_database WHERE datname='{name}'"));
                Assert.Equal(0L, await OmsPostgresTestDatabase.Scalar(admin,
                    $"SELECT count(*) FROM pg_database WHERE datname='{oldTemplate}'"));
            }
        }
        finally
        {
            await OmsPostgresTestDatabase.ValidateServerAsync(admin);
            // No FORCE: unexpected connections or dependencies must fail this test.
            await OmsPostgresTestDatabase.Execute(admin, $"DROP DATABASE IF EXISTS \"{name}\"");
            await OmsPostgresTestDatabase.Execute(admin, $"DROP DATABASE IF EXISTS \"{oldTemplate}\"");
        }
    }
}
