using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TradeOps.Infrastructure.Persistence;

#if TRADEOPS_QUALIFICATION_TOOL
namespace TradeOps.QualificationSupport;
#else
namespace TradeOps.UnitTests.Support;
#endif

internal enum PostgresPreparation { TemplateClone, FreshMigrations }

/// <summary>
/// Only the explicitly marked TradeOps test server is accepted. One server lease bounds
/// disk/connection use across test processes; concurrency INSIDE a case remains real.
/// No ambient transaction, global pools, physical send or persistent infrastructure.
/// </summary>
internal sealed class OmsPostgresTestDatabase : IAsyncDisposable
{
    internal const string ServerMarker = "tradeops-test-server:v1";
    private const string DatabaseMarker = "tradeops-tests:database:v1:";
    private const string TemplateMarker = "tradeops-tests:template:v1:";
    private const long ServerLease = 746219040117L;
    private readonly NpgsqlConnection admin;
    private readonly Stopwatch lifetime = Stopwatch.StartNew();
    private bool holdsLease;
    public NpgsqlDataSource Source { get; private set; } = null!;
    public string ConnectionString { get; private set; } = "";
    public string Name { get; } = "to_test_" + Guid.NewGuid().ToString("N");
    public string? SourceTemplate { get; private set; }
    public static string SchemaFingerprint { get; } = Fingerprint();

    private OmsPostgresTestDatabase(NpgsqlConnection admin) => this.admin = admin;

    public static async Task<OmsPostgresTestDatabase> CreateAsync(
        PostgresPreparation mode = PostgresPreparation.TemplateClone, bool pooling = true) =>
        await CreateForConnectionAsync(Environment.GetEnvironmentVariable("TRADEOPS_B2_POSTGRES"), mode, pooling);

    internal static async Task<OmsPostgresTestDatabase> CreateForConnectionAsync(string? connectionString,
        PostgresPreparation mode = PostgresPreparation.TemplateClone, bool pooling = true)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("POSTGRES_ACCEPTANCE_NOT_RUN: TRADEOPS_B2_POSTGRES is missing. " +
                "OMS PostgreSQL tests MUST FAIL without an isolated PostgreSQL 17 connection.");
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        // Reject accidental use of tradeops_dev even if the host were otherwise marked.
        if (builder.Database != "postgres" || builder.Username != "tradeops_test_admin")
            throw new InvalidOperationException("TEST_SERVER_IDENTITY_REQUIRED: dedicated admin/postgres only.");
        builder.Pooling = false;
        builder.IncludeErrorDetail = false;
        builder.LogParameters = false;
        var admin = new NpgsqlConnection(builder.ConnectionString);
        var fixture = new OmsPostgresTestDatabase(admin);
        try
        {
            await admin.OpenAsync();
            await ValidateServerAsync(admin);
            // Never wait indefinitely for another runner; caller can retry after it exits.
            if (!Convert.ToBoolean(await Scalar(admin, $"SELECT pg_try_advisory_lock({ServerLease})")))
                throw new InvalidOperationException("TEST_SERVER_BUSY: another acceptance owns the server.");
            fixture.holdsLease = true;
            await fixture.CleanAbandonedDatabasesAsync();
            if (mode == PostgresPreparation.TemplateClone)
            {
                fixture.SourceTemplate = await fixture.EnsureTemplateAsync(builder);
                await Execute(admin, $"CREATE DATABASE \"{fixture.Name}\" TEMPLATE \"{fixture.SourceTemplate}\"");
            }
            else
                await Execute(admin, $"CREATE DATABASE \"{fixture.Name}\" TEMPLATE template0");

            await Execute(admin, $"COMMENT ON DATABASE \"{fixture.Name}\" IS '{DatabaseMarker}{fixture.Name}'");
            // Database ACLs are NOT copied from the template. No public access to clones.
            await Execute(admin, $"REVOKE ALL ON DATABASE \"{fixture.Name}\" FROM PUBLIC");
            builder.Database = fixture.Name;
            fixture.ConnectionString = builder.ConnectionString; // explicitly fresh/unpooled escape hatch
            if (mode == PostgresPreparation.FreshMigrations)
                await MigrateAsync(builder.ConnectionString);
            builder.Pooling = pooling;
            builder.MaxPoolSize = 20; // > the 16-way inbox/12-way command races
            builder.MinPoolSize = 0;
            fixture.Source = NpgsqlDataSource.Create(builder.ConnectionString);
            PreparationLog($"PG_FIXTURE mode={mode} pooling={pooling} template={fixture.SourceTemplate ?? "none"} " +
                $"prepare_ms={fixture.lifetime.ElapsedMilliseconds}");
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    internal static async Task ValidateServerAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("""
SELECT current_setting('server_version_num')::integer BETWEEN 170000 AND 179999
 AND current_setting('cluster_name')='tradeops_tests_v1'
 AND current_database()='postgres' AND current_user='tradeops_test_admin'
 AND shobj_description(oid,'pg_database')='tradeops-test-server:v1'
 AND current_setting('fsync')='on' AND current_setting('synchronous_commit')='on'
 AND current_setting('full_page_writes')='on'
 AND current_setting('server_encoding')='UTF8'
 AND datcollate='C' AND datctype='C'
FROM pg_database WHERE datname=current_database()
""", connection);
        if (await command.ExecuteScalarAsync() is not true)
            throw new InvalidOperationException("TEST_SERVER_OWNERSHIP_REQUIRED: marked durable PostgreSQL 17 only.");
    }

    private async Task<string> EnsureTemplateAsync(NpgsqlConnectionStringBuilder builder)
    {
        var name = "to_tpl_" + SchemaFingerprint[..24];
        var marker = TemplateMarker + SchemaFingerprint;
        // At most one completed template. Only owned, marked, idle databases can be removed.
        var ready = false;
        foreach (var entry in await DatabasesAsync("^to_tpl_[0-9a-f]{24}$"))
        {
            if (entry.Name == name && entry.Marker == marker && !entry.AllowConnections)
            {
                ready = true;
                continue;
            }
            if (!System.Text.RegularExpressions.Regex.IsMatch(entry.Marker,
                    "^tradeops-tests:template:v1:[0-9a-f]{64}$") ||
                entry.Name != "to_tpl_" + entry.Marker[TemplateMarker.Length..][..24])
                throw new InvalidOperationException("UNOWNED_TEMPLATE: refusing reset.");
            await DropOwnedAsync(entry.Name, entry.Marker);
        }
        if (ready) return name;
        await Execute(admin, $"CREATE DATABASE \"{name}\" TEMPLATE template0");
        // Mark before migrations, so a failed migration can be recovered on the next run.
        await Execute(admin, $"COMMENT ON DATABASE \"{name}\" IS '{marker}'");
        await Execute(admin, $"REVOKE ALL ON DATABASE \"{name}\" FROM PUBLIC");
        var template = new NpgsqlConnectionStringBuilder(builder.ConnectionString)
            { Database = name, Pooling = false };
        await MigrateAsync(template.ConnectionString);
        // MigrateAsync disposed all unpooled connections. Freeze source against accidental writes.
        await Execute(admin, $"ALTER DATABASE \"{name}\" ALLOW_CONNECTIONS false");
        Console.WriteLine("PG_TEMPLATE_CREATED fingerprint=" + SchemaFingerprint);
        return name;
    }

    private async Task CleanAbandonedDatabasesAsync()
    {
        foreach (var entry in await DatabasesAsync("^to_test_[0-9a-f]{32}$"))
        {
            if (entry.Marker != DatabaseMarker + entry.Name)
                throw new InvalidOperationException("UNOWNED_TEST_DATABASE: refusing cleanup.");
            await DropOwnedAsync(entry.Name, entry.Marker);
        }
        // A killed restricted-role test can leave a cluster role after its database is removed.
        // DROP ROLE itself refuses foreign dependencies; no DROP OWNED against other databases.
        var roles = new List<string>();
        await using (var command = new NpgsqlCommand("""
SELECT rolname FROM pg_roles
WHERE rolname ~ '^to_app_[0-9a-f]{32}$'
 AND shobj_description(oid,'pg_authid') ~ '^tradeops-tests:role:v1:to_test_[0-9a-f]{32}$'
 AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication
""", admin))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) roles.Add(reader.GetString(0));
        foreach (var role in roles)
            await Execute(admin, $"DROP ROLE \"{role}\"");
    }

    private async Task<List<(string Name, string Marker, bool AllowConnections)>> DatabasesAsync(string regex)
    {
        var result = new List<(string, string, bool)>();
        await using var command = new NpgsqlCommand("""
SELECT datname, coalesce(shobj_description(oid,'pg_database'),''), datallowconn
FROM pg_database WHERE datname ~ @regex AND datdba=(SELECT oid FROM pg_roles WHERE rolname=current_user)
ORDER BY datname
""", admin);
        command.Parameters.AddWithValue("regex", regex);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add((reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
        return result;
    }

    private async Task DropOwnedAsync(string name, string marker)
    {
        await ValidateServerAsync(admin);
        await using var check = new NpgsqlCommand("""
SELECT shobj_description(oid,'pg_database')=@marker
 AND datdba=(SELECT oid FROM pg_roles WHERE rolname=current_user)
 AND NOT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=@name)
FROM pg_database WHERE datname=@name
""", admin);
        check.Parameters.AddWithValue("name", name);
        check.Parameters.AddWithValue("marker", marker);
        var owned = await check.ExecuteScalarAsync();
        if (owned is null) return;
        if (owned is not true || !(System.Text.RegularExpressions.Regex.IsMatch(name, "^to_test_[0-9a-f]{32}$") ||
            System.Text.RegularExpressions.Regex.IsMatch(name, "^to_tpl_[0-9a-f]{24}$")))
            throw new InvalidOperationException("TEST_RESOURCE_OWNERSHIP_OR_IDLE_CHECK_FAILED: refusing DROP.");
        // No FORCE and no global pool reset: leaked connections must fail acceptance.
        await Execute(admin, $"DROP DATABASE \"{name}\"");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Source is not null) await Source.DisposeAsync();
            if (holdsLease && admin.State == System.Data.ConnectionState.Open)
            {
                // Closing a socket can precede the backend disappearing from pg_stat_activity.
                // Wait only for THIS database; never terminate sessions or weaken DROP's idle check.
                var closing = Stopwatch.StartNew();
                while (closing.Elapsed < TimeSpan.FromSeconds(3))
                {
                    await using var check = new NpgsqlCommand(
                        "SELECT count(*) FROM pg_stat_activity WHERE datname=@name", admin);
                    check.Parameters.AddWithValue("name", Name);
                    if (Convert.ToInt64(await check.ExecuteScalarAsync()) == 0) break;
                    await Task.Delay(50);
                }
                await DropOwnedAsync(Name, DatabaseMarker + Name);
            }
        }
        finally
        {
            await admin.DisposeAsync(); // releases server lease even on failure
            PreparationLog($"PG_FIXTURE total_ms={lifetime.ElapsedMilliseconds}");
        }
    }

    private static async Task MigrateAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<TradeOpsDbContext>().UseNpgsql(connectionString).Options;
        await using var context = new TradeOpsDbContext(options);
        await context.Database.MigrateAsync();
    }

    private static string Fingerprint()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // Bump seed/config version for extensions, initdb or fixture seed changes.
        hash.AppendData(Encoding.UTF8.GetBytes("fixture-seed-v1;UTF8;C;template0;no-extra-extensions;"));
        foreach (var dependency in new[] { typeof(DbContext).Assembly,
                     typeof(RelationalDatabaseFacadeExtensions).Assembly, typeof(NpgsqlConnection).Assembly,
                     Assembly.Load("Npgsql.EntityFrameworkCore.PostgreSQL") })
        {
            // AssemblyVersion can stay constant across package patches; use the actual package build.
            var version = dependency.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? dependency.GetName().Version?.ToString();
            hash.AppendData(Encoding.UTF8.GetBytes(dependency.GetName().Name + ":" + version + "\0"));
        }
        var assembly = typeof(OmsPostgresTestDatabase).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(x => x.StartsWith("TradeOps.TestSchema.", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        if (resources.Length == 0) throw new InvalidOperationException("TEST_SCHEMA_INPUTS_MISSING");
        foreach (var resource in resources)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(resource + "\0"));
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            // OS checkout line endings must not invalidate an identical schema.
            hash.AppendData(Encoding.UTF8.GetBytes(reader.ReadToEnd().Replace("\r\n", "\n") + "\0"));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void PreparationLog(string message)
    {
#if !TRADEOPS_QUALIFICATION_TOOL
        Console.WriteLine(message);
#endif
    }

    internal static async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    internal static async Task<object?> Scalar(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }
}
