using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using TradeOps.Domain.Oms;
using TradeOps.Infrastructure.Persistence.Oms;

namespace TradeOps.DeribitMainnetEthQualification;

// Qualification binding only. No production registration, migrations or venue operations.
internal static class RecoveryStore
{
    internal const string Role = "tradeops_q_recovery_v1";
    private const string Marker = "tradeops-mainnet-qualification:reader:v1";

    internal static AccountKey Scope(long nativeId, string accountType)
    {
        if (nativeId <= 0 || accountType is not ("main" or "subaccount"))
            throw new InvalidOperationException("Encrypted native binding required.");
        var native = "observed-native:" + nativeId.ToString(CultureInfo.InvariantCulture);
        return new(new("local-mainnet-qualification"), new("DERIBIT"), OmsEnvironment.Mainnet,
            new(native), accountType == "subaccount" ? new TradingSubaccountId(native) : null);
    }

    internal static async Task<int> RunAsync(string mode, CancellationToken ct)
    {
        try
        {
            var raw = Environment.GetEnvironmentVariable("TRADEOPS_RECOVERY_POSTGRES");
            if (string.IsNullOrWhiteSpace(raw)) throw new InvalidOperationException();
            var config = new NpgsqlConnectionStringBuilder(raw) { IncludeErrorDetail = false,
                LogParameters = false, SearchPath = "public", Pooling = false, Timeout = 10, CommandTimeout = 20 };
            var provision = mode == "--provision-reader";
            if (config.Database != "tradeops_mainnet_qualification" ||
                config.Username != (provision ? "tradeops_qualification_admin" : Role)) throw new InvalidOperationException();
            await using var source = NpgsqlDataSource.Create(config.ConnectionString);
            await ValidateOwnership(source, ct);
            if (provision)
            {
                await Provision(source, config.Password ?? throw new InvalidOperationException(), ct);
                var runtimeConfig = new NpgsqlConnectionStringBuilder(config.ConnectionString)
                    { Username = Role, Password = Password(config.Password!) };
                await using var runtime = NpgsqlDataSource.Create(runtimeConfig.ConnectionString);
                await ValidateRuntime(runtime, ct);
                Console.WriteLine("{\"Status\":\"RECOVERY_READER_PROVISIONED\",\"Migrations\":0,\"TradingReady\":false}");
                return 0;
            }
            // Bound input contains only the DPAPI native pin, never keys or DB-selected identity.
            using var input = new MemoryStream();
            var buffer = new byte[512]; int n;
            var stdin = Console.OpenStandardInput();
            while ((n = await stdin.ReadAsync(buffer, ct)) != 0)
            {
                if (input.Length + n > 2048) throw new InvalidOperationException();
                input.Write(buffer, 0, n);
            }
            using var pin = JsonDocument.Parse(input.ToArray());
            var scope = Scope(pin.RootElement.GetProperty("ExpectedAccountId").GetInt64(),
                pin.RootElement.GetProperty("AccountType").GetString()!);
            await ValidateRuntime(source, ct);
            var evidence = await new OmsPostgresPrivateRecoveryReader(source).ReadAsync(scope, cancellationToken: ct);
            var facts = evidence.Observations.Select(x => x.Envelope.Facts).ToArray();
            var passed = evidence.IsUsable && evidence.AcceptedFactCount == 2 && facts.Length == 2 &&
                facts.Count(x => x.OrderState == "open") == 1 && facts.Count(x => x.OrderState == "cancelled") == 1 &&
                facts.All(x => x.NativeInstrument == "ETH-PERPETUAL" && x.NativeAmount == 1m && x.FilledNativeAmount == 0m) &&
                facts.Select(x => x.ExchangeOrderId).Distinct().Count() == 1;
            Console.WriteLine(JsonSerializer.Serialize(new { Status = passed ? "RETAINED_SNAPSHOT_PASS" : "RETAINED_SNAPSHOT_REFUSED",
                evidence.AcceptedFactCount, ObservationCount = facts.Length, BlockingProblems = evidence.Problems.Count,
                PersistentStore = true, SelectOnly = true, VenueRequests = 0, MutatingVenueCalls = 0,
                FreshVenueReconciliation = false, TradingReady = false }));
            return passed ? 0 : 1;
        }
        catch
        {
            Console.WriteLine("{\"Status\":\"RECOVERY_STORE_REFUSED\",\"TradingReady\":false}");
            return 1; // Never print driver errors, connection strings, payloads or native identifiers.
        }
    }

    internal static async Task ValidateOwnership(NpgsqlDataSource source, CancellationToken ct = default)
    {
        await using var command = source.CreateCommand("""
SELECT current_database()='tradeops_mainnet_qualification'
 AND current_setting('cluster_name')='tradeops_mainnet_qualification_v1'
 AND shobj_description(oid,'pg_database')='tradeops-mainnet-qualification:v1'
 AND pg_get_userbyid(datdba)='tradeops_qualification_admin'
 AND current_setting('server_version_num')::int BETWEEN 170000 AND 179999
 AND current_setting('fsync')='on' AND current_setting('synchronous_commit')='on'
 AND current_setting('full_page_writes')='on'
FROM pg_database WHERE datname=current_database()
""");
        if (await command.ExecuteScalarAsync(ct) is not true) throw new InvalidOperationException("Store refused.");
    }

    internal static async Task ValidateRuntime(NpgsqlDataSource source, CancellationToken ct = default)
    {
        await using var command = source.CreateCommand("""
SELECT current_user=session_user AND rolcanlogin
 AND shobj_description(pg_roles.oid,'pg_authid')='tradeops-mainnet-qualification:reader:v1'
 AND NOT (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls)
 AND NOT EXISTS(SELECT 1 FROM pg_auth_members WHERE member=pg_roles.oid)
 AND NOT has_database_privilege(current_user,current_database(),'CREATE,TEMP')
 AND NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname NOT LIKE 'pg_%' AND nspname<>'information_schema'
   AND has_schema_privilege(current_user,oid,'CREATE'))
 AND NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
   WHERE n.nspname NOT LIKE 'pg_%' AND n.nspname<>'information_schema' AND c.relkind IN ('r','p','v','m','f')
   AND has_table_privilege(current_user,c.oid,'INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER'))
 AND NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
   WHERE n.nspname NOT LIKE 'pg_%' AND c.relkind='S' AND has_sequence_privilege(current_user,c.oid,'USAGE,UPDATE'))
 AND NOT EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
   WHERE n.nspname NOT LIKE 'pg_%' AND n.nspname<>'information_schema' AND p.prorettype<>'trigger'::regtype
   AND has_function_privilege(current_user,p.oid,'EXECUTE'))
 AND has_table_privilege(current_user,'public.oms_inbox','SELECT')
 AND has_table_privilege(current_user,'public.oms_private_capture_observations','SELECT')
 AND has_table_privilege(current_user,'public.oms_integrity_incidents','SELECT')
FROM pg_roles WHERE rolname=current_user
""");
        if (await command.ExecuteScalarAsync(ct) is not true) throw new InvalidOperationException("SELECT-only role refused.");
    }

    internal static string Password(string adminPassword) => Convert.ToHexStringLower(HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(adminPassword), Encoding.UTF8.GetBytes(Role)));

    internal static async Task Provision(NpgsqlDataSource source, string adminPassword, CancellationToken ct = default)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using var check = new NpgsqlCommand("SELECT shobj_description(oid,'pg_authid') FROM pg_roles WHERE rolname=@name", connection, tx);
        check.Parameters.AddWithValue("name", Role);
        var existing = await check.ExecuteScalarAsync(ct);
        if (existing is not null && existing as string != Marker) throw new InvalidOperationException();
        // Existing roles are validated through the runtime gate, never silently repaired.
        if (existing is null)
        {
            var dbName = connection.Database.Replace("\"", "\"\"");
            await using (var logging = new NpgsqlCommand("SET LOCAL log_statement=none; SET LOCAL log_min_error_statement=panic; SET LOCAL log_parameter_max_length_on_error=0", connection, tx))
                await logging.ExecuteNonQueryAsync(ct);
            await using var create = new NpgsqlCommand($"""
CREATE ROLE "{Role}" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{Password(adminPassword)}';
COMMENT ON ROLE "{Role}" IS '{Marker}';
REVOKE TEMP ON DATABASE "{dbName}" FROM PUBLIC;
GRANT CONNECT ON DATABASE "{dbName}" TO "{Role}";
GRANT USAGE ON SCHEMA public TO "{Role}";
GRANT SELECT ON public.oms_inbox,public.oms_private_capture_observations,public.oms_integrity_incidents TO "{Role}";
ALTER ROLE "{Role}" SET default_transaction_read_only=on;
""", connection, tx);
            await create.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }
}
