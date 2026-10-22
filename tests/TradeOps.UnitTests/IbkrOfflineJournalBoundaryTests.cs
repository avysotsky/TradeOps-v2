using TradeOps.Infrastructure.Exchange.Ibkr;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class IbkrOfflineJournalBoundaryTests
{
    private const string TestSchema = "ibkr_smoke_0123456789abcdef0123456789abcdef";
    private const string SafeConnection =
        "Host=localhost;Database=tradeops_ibkr_offline_smoke_unit;Username=offline;" +
        "Search Path=" + TestSchema;

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void DisposableLoopbackDatabaseWithIsolatedSchema_IsAccepted(string host)
    {
        var connection = SafeConnection.Replace("Host=localhost", "Host=" + host,
            StringComparison.Ordinal);
        var validated = IbkrOfflineJournalConnectionPolicy.Validate(connection);
        Assert.Contains("tradeops_ibkr_offline_smoke_unit", validated,
            StringComparison.Ordinal);
        Assert.Contains(TestSchema, validated, StringComparison.Ordinal);
        Assert.NotNull(new IbkrOfflinePostgresJournal(connection));
    }

    [Theory]
    [InlineData("Host=localhost;Database=production;Username=offline;Search Path=ibkr_smoke_0123456789abcdef0123456789abcdef")]
    [InlineData("Host=192.168.0.10;Database=tradeops_ibkr_offline_smoke_unit;Search Path=ibkr_smoke_0123456789abcdef0123456789abcdef")]
    [InlineData("Host=example.com;Database=tradeops_ibkr_offline_smoke_unit;Search Path=ibkr_smoke_0123456789abcdef0123456789abcdef")]
    [InlineData("Host=localhost,192.168.0.10;Database=tradeops_ibkr_offline_smoke_unit;Search Path=ibkr_smoke_0123456789abcdef0123456789abcdef")]
    [InlineData("Host=/var/run/postgresql;Database=tradeops_ibkr_offline_smoke_unit;Search Path=ibkr_smoke_0123456789abcdef0123456789abcdef")]
    [InlineData("Host=localhost;Database=tradeops_ibkr_offline_smoke_;Search Path=ibkr_smoke_0123456789abcdef0123456789abcdef")]
    [InlineData("Host=localhost;Database=tradeops_ibkr_offline_smoke_unit;Search Path=public")]
    [InlineData("Host=localhost;Database=tradeops_ibkr_offline_smoke_unit")]
    [InlineData("Host=localhost;Database=tradeops_ibkr_offline_smoke_unit;Search Path=ibkr_smoke_0123456789abcdef0123456789abcdef,public")]
    [InlineData("Host=localhost;Database=tradeops_ibkr_offline_smoke_unit;Search Path=public,ibkr_smoke_0123456789abcdef0123456789abcdef")]
    [InlineData("Host=localhost;Database=tradeops_ibkr_offline_smoke_unit;Search Path=ibkr_smoke_0123456789abcdef0123456789abcdeg")]
    [InlineData("Host=localhost;Database=tradeops_ibkr_offline_smoke_unit;Search Path=ibkr_smoke_0123456789abcdef0123456789abcdef;Options=-c search_path=public")]
    public void NonDisposableRemoteOrSchemaOverride_IsRejectedBeforeAnyConnection(string connection)
    {
        Assert.Throws<InvalidOperationException>(() =>
            IbkrOfflineJournalConnectionPolicy.Validate(connection));
        Assert.Throws<InvalidOperationException>(() =>
            new IbkrOfflinePostgresJournal(connection));
    }

    [Fact]
    public void ConnectionPolicyFailure_DoesNotRevealCredentials()
    {
        const string secret = "NeverEchoThisSecret";
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new IbkrOfflinePostgresJournal(
                "Host=production.example;Database=production;Password=" + secret));
        Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(IbkrOfflineOrderEventType.CumulativeFill)]
    [InlineData(IbkrOfflineOrderEventType.ReconciliationNotFound)]
    [InlineData(IbkrOfflineOrderEventType.ReconciledWorking)]
    [InlineData(IbkrOfflineOrderEventType.ReconciledCancelled)]
    [InlineData(IbkrOfflineOrderEventType.ReconciledFilled)]
    [InlineData(IbkrOfflineOrderEventType.CancelConfirmed)]
    public void EvidenceBearingEvents_CannotBeAppendedDirectly(
        IbkrOfflineOrderEventType type)
    {
        var evt = new IbkrOfflineOrderEvent("coid-1", type,
            BrokerOrderId: "broker-1", CumulativeFilled: 1m);
        Assert.Throws<InvalidOperationException>(() =>
            IbkrOfflineJournalReplay.ValidateDirectAppend(evt));
    }

    [Theory]
    [InlineData(IbkrOfflineOrderEventType.PreflightApproved)]
    [InlineData(IbkrOfflineOrderEventType.SubmissionStarted)]
    [InlineData(IbkrOfflineOrderEventType.TransportTimedOut)]
    [InlineData(IbkrOfflineOrderEventType.BrokerAcknowledged)]
    [InlineData(IbkrOfflineOrderEventType.CancelRequested)]
    public void NonEvidenceBearingSyntheticEvents_CanUseOrdinaryJournalPath(
        IbkrOfflineOrderEventType type)
    {
        IbkrOfflineJournalReplay.ValidateDirectAppend(
            new IbkrOfflineOrderEvent("coid-1", type));
    }
}
