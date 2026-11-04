using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TradeOps.Infrastructure.Persistence;
using TradeOps.QualificationSupport;
using CaptureRole = TradeOps.DeribitLiveInboxQualification.LiveInboxQualification.CaptureRole;

namespace TradeOps.DeribitTestnetOrderQualification;

internal sealed class QualificationDatabase : IAsyncDisposable
{
    internal NpgsqlDataSource Journal { get; private set; } = null!;
    internal NpgsqlDataSource Capture { get; private set; } = null!;
    internal bool Persistent { get; private init; }
    private OmsPostgresTestDatabase? fixture;
    private CaptureRole? role;

    internal static async Task<QualificationDatabase> OpenAsync(bool mainnet, CancellationToken ct)
    {
        if (!mainnet)
        {
            var fixture = await OmsPostgresTestDatabase.CreateAsync();
            try
            {
                var role = await CaptureRole.CreateAsync(fixture, ct);
                return new() { fixture = fixture, role = role, Journal = fixture.Source, Capture = role.Source };
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        var raw = Environment.GetEnvironmentVariable("TRADEOPS_MAINNET_QUALIFICATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(raw)) throw new InvalidOperationException("Persistent qualification store required.");
        var config = new NpgsqlConnectionStringBuilder(raw) { IncludeErrorDetail = false, LogParameters = false,
            SearchPath = "public", Pooling = true, MaxPoolSize = 4, Timeout = 10, CommandTimeout = 30 };
        if (config.Database != "tradeops_mainnet_qualification" || config.Username != "tradeops_qualification_admin")
            throw new InvalidOperationException("Owned mainnet qualification database required.");
        await using var adminSource = NpgsqlDataSource.Create(config.ConnectionString);
        await using var admin = await adminSource.OpenConnectionAsync(ct);
        await using (var verify = new NpgsqlCommand("""
SELECT current_setting('cluster_name')='tradeops_mainnet_qualification_v1'
 AND shobj_description(oid,'pg_database')='tradeops-mainnet-qualification:v1'
 AND current_setting('fsync')='on' AND current_setting('synchronous_commit')='on'
 AND current_setting('full_page_writes')='on'
FROM pg_database WHERE datname=current_database()
""", admin))
            if (await verify.ExecuteScalarAsync(ct) is not true) throw new InvalidOperationException("Store ownership/durability refused.");
        // Migration targets only this marked, separately owned database. Never a dev/production database.
        await using (var context = new TradeOpsDbContext(new DbContextOptionsBuilder<TradeOpsDbContext>()
            .UseNpgsql(config.ConnectionString).Options)) await context.Database.MigrateAsync(ct);
        await Execute(admin, "SET log_error_verbosity=terse; SET log_min_error_statement=panic; SET log_parameter_max_length_on_error=0; SET log_statement=none", ct);
        var result = new QualificationDatabase { Persistent = true };
        try
        {
            result.Capture = await RuntimeRole("tradeops_q_capture_v1", capture: true);
            result.Journal = await RuntimeRole("tradeops_q_journal_v1", capture: false);
            await using var journalConnection = await result.Journal.OpenConnectionAsync(ct);
            await using var privilegeCheck = new NpgsqlCommand("""
SELECT NOT (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls)
 AND current_user=session_user AND NOT has_schema_privilege(current_user,'public','CREATE')
 AND NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
 WHERE n.nspname='public' AND c.relname IN ('Orders','Fills','oms_inbox','oms_private_capture_observations','oms_account_leases')
 AND has_table_privilege(current_user,c.oid,'INSERT,UPDATE,DELETE,TRUNCATE'))
 AND NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
 WHERE n.nspname='public' AND c.relkind IN ('r','p') AND has_table_privilege(current_user,c.oid,'DELETE,TRUNCATE'))
FROM pg_roles WHERE rolname=current_user
""", journalConnection);
            if (await privilegeCheck.ExecuteScalarAsync(ct) is not true) throw new InvalidOperationException("Journal runtime role gate refused.");
            return result;
        }
        catch { await result.DisposeAsync(); throw; }

        async Task<NpgsqlDataSource> RuntimeRole(string name, bool capture)
        {
            const string marker = "tradeops-mainnet-qualification:runtime:v1";
            await using (var existing = new NpgsqlCommand("SELECT shobj_description(oid,'pg_authid') FROM pg_roles WHERE rolname=@name", admin))
            {
                existing.Parameters.AddWithValue("name", name);
                var value = await existing.ExecuteScalarAsync(ct);
                if (value is not null && value as string != marker) throw new InvalidOperationException("Unowned runtime role refused.");
                if (value is null)
                    await Execute(admin, $"CREATE ROLE \"{name}\" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS; COMMENT ON ROLE \"{name}\" IS '{marker}'", ct);
            }
            var password = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(config.Password!), Encoding.UTF8.GetBytes(name)));
            await Execute(admin, $"""
ALTER ROLE "{name}" PASSWORD '{password}';
ALTER ROLE "{name}" SET log_error_verbosity=terse;
ALTER ROLE "{name}" SET log_min_error_statement=panic;
ALTER ROLE "{name}" SET log_parameter_max_length_on_error=0;
ALTER ROLE "{name}" SET log_statement=none;
GRANT CONNECT ON DATABASE tradeops_mainnet_qualification TO "{name}";
GRANT USAGE ON SCHEMA public TO "{name}";
GRANT SELECT,INSERT ON oms_integrity_incidents TO "{name}";
GRANT USAGE ON SEQUENCE oms_integrity_incidents_incident_id_seq TO "{name}";
""", ct);
            await Execute(admin, capture
                ? $"GRANT SELECT,INSERT,UPDATE ON oms_inbox,oms_private_capture_observations TO \"{name}\""
                : $"GRANT SELECT,INSERT,UPDATE ON oms_aggregates,oms_intents,oms_commands,oms_outbox,oms_attempts TO \"{name}\"", ct);
            var settings = new NpgsqlConnectionStringBuilder(config.ConnectionString) { Username = name, Password = password, MaxPoolSize = 8 };
            return NpgsqlDataSource.Create(settings.ConnectionString);
        }
    }

    private static async Task Execute(NpgsqlConnection connection, string sql, CancellationToken ct)
    { await using var command = new NpgsqlCommand(sql, connection); await command.ExecuteNonQueryAsync(ct); }

    public async ValueTask DisposeAsync()
    {
        if (fixture is not null)
        {
            if (role is not null) await role.DisposeAsync();
            await fixture.DisposeAsync();
        }
        else
        {
            if (Capture is not null) await Capture.DisposeAsync();
            if (Journal is not null) await Journal.DisposeAsync();
            // Mainnet journal/inbox/roles/database/volume are intentionally retained for recovery.
        }
    }
}
