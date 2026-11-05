using Npgsql;
using TradeOps.DeribitMainnetEthQualification;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;
using static TradeOps.UnitTests.DeribitOrderRecoveryProjectorTests;

namespace TradeOps.UnitTests;

public sealed class OmsRecoveryStoreBindingTests
{
    [Theory]
    [InlineData(0, "main")] [InlineData(-1, "main")] [InlineData(1, "unknown")]
    public void InvalidNativePinRefused(long id, string type) =>
        Assert.Throws<InvalidOperationException>(() => RecoveryStore.Scope(id, type));

    [Theory]
    [InlineData("main", false)] [InlineData("subaccount", true)]
    public void ScopeRetainsNativeHierarchy(string type, bool subaccount)
    {
        var scope = RecoveryStore.Scope(123, type);
        Assert.Equal("observed-native:123", scope.Account.Value);
        Assert.Equal(subaccount, scope.Subaccount is not null);
        Assert.Equal("local-mainnet-qualification", scope.Tenant.Value);
    }

    [Fact]
    public async Task DisposableCloneCannotMasqueradeAsRetainedStore()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => RecoveryStore.ValidateOwnership(db.Source));
    }

    [Theory]
    [InlineData("none")] [InlineData("insert")] [InlineData("membership")]
    [InlineData("temp")] [InlineData("function")] [InlineData("schema")]
    public async Task ProvisionAndRuntimeGateUseEffectivePrivileges(string extra)
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(pooling: false);
        const string role = RecoveryStore.Role;
        const string adminPassword = "clone-only-seed";
        await using (var absent = db.Source.CreateCommand($"SELECT count(*) FROM pg_roles WHERE rolname='{role}'"))
            Assert.Equal(0L, await absent.ExecuteScalarAsync()); // Never clean up an existing cluster role.
        try
        {
            await RecoveryStore.Provision(db.Source, adminPassword);
            await RecoveryStore.Provision(db.Source, adminPassword); // idempotent, no migrations
            await new OmsPostgresPrivateEventCapture(db.Source).CaptureAsync(Report());
            var sql = extra switch {
                "insert" => $"GRANT INSERT ON oms_inbox TO {role}",
                "membership" => $"GRANT tradeops_test_admin TO {role}",
                "temp" => $"GRANT TEMP ON DATABASE \"{db.Name}\" TO {role}",
                "function" => "CREATE FUNCTION public.fixture_write() RETURNS int LANGUAGE sql AS 'SELECT 1'",
                "schema" => $"GRANT CREATE ON SCHEMA public TO {role}",
                _ => "SELECT 1" };
            await using (var cmd = db.Source.CreateCommand(sql)) await cmd.ExecuteNonQueryAsync();
            var settings = new NpgsqlConnectionStringBuilder(db.ConnectionString) { Username = role,
                Password = RecoveryStore.Password(adminPassword), Pooling = false };
            await using var runtime = NpgsqlDataSource.Create(settings.ConnectionString);
            if (extra != "none")
                await Assert.ThrowsAsync<InvalidOperationException>(() => RecoveryStore.ValidateRuntime(runtime));
            else
            {
                await RecoveryStore.ValidateRuntime(runtime);
                var evidence = await new OmsPostgresPrivateRecoveryReader(runtime).ReadAsync(Account);
                Assert.True(evidence.IsUsable);
                Assert.Single(evidence.Observations);
                await using var write = runtime.CreateCommand("DELETE FROM oms_inbox");
                await Assert.ThrowsAsync<PostgresException>(() => write.ExecuteNonQueryAsync());
            }
        }
        finally
        {
            await using var cleanup = db.Source.CreateCommand($"REVOKE tradeops_test_admin FROM {role}; DROP OWNED BY {role}; DROP ROLE {role}");
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
