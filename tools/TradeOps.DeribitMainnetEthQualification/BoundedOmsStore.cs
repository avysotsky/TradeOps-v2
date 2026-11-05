using Npgsql;
using TradeOps.Infrastructure.Persistence.Oms;

namespace TradeOps.DeribitMainnetEthQualification;

// Existing owned store/roles only. This path NEVER calls the legacy migrating/admin opener.
internal sealed class BoundedOmsStore : IAsyncDisposable
{
    internal NpgsqlDataSource Journal { get; }
    internal NpgsqlDataSource Capture { get; }
    internal NpgsqlDataSource Reader { get; }
    internal bool Persistent { get; }
    private readonly IAsyncDisposable? resource;
    internal BoundedOmsStore(NpgsqlDataSource journal, NpgsqlDataSource capture, NpgsqlDataSource reader,
        bool persistent, IAsyncDisposable? resource = null)
    { Journal = journal; Capture = capture; Reader = reader; Persistent = persistent; this.resource = resource; }

    internal static async Task<BoundedOmsStore> OpenExistingAsync(CancellationToken ct)
    {
        var configurations = new[] {
            Configuration("JOURNAL", "tradeops_q_bounded_journal_v1"),
            Configuration("CAPTURE", "tradeops_q_capture_v1"),
            Configuration("READER", "tradeops_q_bounded_reader_v1") };
        if (configurations.Any(x => x.Host != configurations[0].Host || x.Port != configurations[0].Port))
            throw new InvalidOperationException("One owned qualification server required.");
        var sources = configurations.Select(x => NpgsqlDataSource.Create(x.ConnectionString)).ToArray();
        try
        {
            foreach (var source in sources) await RecoveryStore.ValidateOwnership(source, ct);
            for (var i = 0; i < sources.Length; i++)
            {
                await using var db = await sources[i].OpenConnectionAsync(ct);
                await using var role = new NpgsqlCommand("""
SELECT current_user=session_user AND rolcanlogin AND NOT(rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls)
 AND shobj_description(oid,'pg_authid')=@marker
 AND NOT EXISTS(SELECT 1 FROM pg_auth_members WHERE member=pg_roles.oid)
 AND NOT has_database_privilege(current_user,current_database(),'CREATE,TEMP')
 AND NOT has_schema_privilege(current_user,'public','CREATE')
 AND NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public'
  AND c.relkind IN ('r','p') AND has_table_privilege(current_user,c.oid,'DELETE,TRUNCATE'))
 AND to_regclass('public.oms_deribit_command_receipts_v1') IS NOT NULL
FROM pg_roles WHERE rolname=current_user
""", db);
                role.Parameters.AddWithValue("marker", i == 1 ? "tradeops-mainnet-qualification:runtime:v1" : "tradeops-mainnet-qualification:bounded-oms:v1");
                if (await role.ExecuteScalarAsync(ct) is not true) throw new InvalidOperationException("Bounded role/schema not provisioned.");
                if (i == 0)
                {
                    await using var permissions = new NpgsqlCommand("""
SELECT (SELECT bool_and(has_table_privilege(current_user,relation,privilege)) FROM (VALUES
 ('oms_aggregates','SELECT'),('oms_aggregates','INSERT'),('oms_aggregates','UPDATE'),
 ('oms_intents','SELECT'),('oms_intents','INSERT'),('oms_commands','SELECT'),('oms_commands','INSERT'),('oms_commands','UPDATE'),
 ('oms_attempts','SELECT'),('oms_attempts','INSERT'),('oms_attempts','UPDATE'),
 ('oms_outbox','SELECT'),('oms_outbox','INSERT'),('oms_outbox','UPDATE'),
 ('oms_deribit_command_receipts_v1','SELECT'),('oms_deribit_command_receipts_v1','INSERT'),
 ('oms_integrity_incidents','SELECT'),('oms_integrity_incidents','INSERT')) required(relation,privilege))
 AND has_sequence_privilege(current_user,'oms_integrity_incidents_incident_id_seq','USAGE')
 AND NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public'
  AND c.relname IN ('Orders','Fills','oms_inbox','oms_private_capture_observations','oms_account_leases')
  AND has_table_privilege(current_user,c.oid,'INSERT,UPDATE,DELETE,TRUNCATE'))
""", db);
                    if (await permissions.ExecuteScalarAsync(ct) is not true) throw new InvalidOperationException("Journal authority refused.");
                }
                if (i == 2)
                {
                    await using var readOnly = new NpgsqlCommand("""
SELECT NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
 WHERE n.nspname NOT LIKE 'pg_%' AND n.nspname<>'information_schema' AND c.relkind IN ('r','p','v','m','f')
 AND has_table_privilege(current_user,c.oid,'INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER'))
 AND NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public'
 AND c.relkind='S' AND has_sequence_privilege(current_user,c.oid,'USAGE,UPDATE'))
 AND (SELECT bool_and(has_table_privilege(current_user,table_name,'SELECT')) FROM unnest(ARRAY[
 'oms_commands','oms_intents','oms_aggregates','oms_attempts','oms_outbox','oms_inbox',
 'oms_private_capture_observations','oms_integrity_incidents','oms_deribit_command_receipts_v1']) AS tables(table_name))
""", db);
                    if (await readOnly.ExecuteScalarAsync(ct) is not true) throw new InvalidOperationException("Reader authority refused.");
                }
            }
            return new(sources[0], sources[1], sources[2], true);
        }
        catch { foreach (var source in sources) await source.DisposeAsync(); throw; }

        static NpgsqlConnectionStringBuilder Configuration(string suffix, string role)
        {
            var raw = Environment.GetEnvironmentVariable("TRADEOPS_BOUNDED_OMS_" + suffix + "_POSTGRES");
            if (string.IsNullOrWhiteSpace(raw)) throw new InvalidOperationException("Bounded store requires explicit provisioning.");
            var config = new NpgsqlConnectionStringBuilder(raw) { Pooling = false, IncludeErrorDetail = false,
                LogParameters = false, SearchPath = "public", Timeout = 10, CommandTimeout = 20 };
            if (config.Database != "tradeops_mainnet_qualification" || config.Username != role)
                throw new InvalidOperationException("Wrong bounded qualification database/role.");
            return config;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (resource is not null) await resource.DisposeAsync();
        else { await Journal.DisposeAsync(); await Capture.DisposeAsync(); await Reader.DisposeAsync(); }
    }
}
