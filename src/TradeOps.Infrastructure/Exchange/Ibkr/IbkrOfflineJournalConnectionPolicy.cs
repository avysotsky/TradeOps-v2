using System.Text.RegularExpressions;
using Npgsql;

namespace TradeOps.Infrastructure.Exchange.Ibkr;

/// <summary>
/// Account-free PostgreSQL journal safety boundary. Rejects non-disposable
/// database targets BEFORE a journal can open a connection or write anything.
/// This is defense-in-depth, not a replacement for database role isolation.
/// </summary>
public static class IbkrOfflineJournalConnectionPolicy
{
    private const string RequiredDatabasePrefix = "tradeops_ibkr_offline_smoke_";

    public static string Validate(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            throw Invalid();
        }

        if (!IsLoopbackOnly(builder.Host)
            || string.IsNullOrEmpty(builder.Database)
            || !builder.Database.StartsWith(RequiredDatabasePrefix, StringComparison.Ordinal)
            || builder.Database.Length == RequiredDatabasePrefix.Length
            || builder.Database.Length > 63
            || !builder.Database.All(c => c is >= 'a' and <= 'z'
                or >= '0' and <= '9' or '_')
            || !Regex.IsMatch(builder.SearchPath ?? string.Empty,
                @"\Aibkr_smoke_[0-9a-f]{32}\z", RegexOptions.CultureInvariant)
            || !string.IsNullOrWhiteSpace(builder.Options))
        {
            throw Invalid();
        }

        return builder.ConnectionString;
    }

    private static bool IsLoopbackOnly(string? host) =>
        host is not null && (
            string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(host, "::1", StringComparison.Ordinal));

    private static InvalidOperationException Invalid() =>
        new("IBKR offline journal requires an isolated disposable loopback PostgreSQL database and unique smoke schema.");
}
