using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Exchange.Ibkr;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class IbkrOfflineExecutionBaselinePlannerTests
{
    private const string Account = "offline-account";
    private const string Coid = "to-coid-1";
    private const string Broker = "synthetic-broker-1";

    [Fact]
    public void CompleteExecutionEvidence_ImportsOnlyPreviouslyUnseenIds()
    {
        var earlier = new IbkrOfflineJournalExecution("exec-1", Broker, 2m);
        var order = Evidence(
            5m,
            [
                Execution("exec-1", 2m),
                Execution("exec-2", 3m),
                Execution("exec-2", 3m) // identical broker replay, count once
            ]);
        var delta = IbkrOfflineExecutionBaselinePlanner.Plan([earlier], order);

        var added = Assert.Single(delta);
        Assert.Equal("exec-2", added.ExecutionId);
        Assert.Equal(3m, added.Quantity);
        Assert.Equal(Broker, added.BrokerOrderId);
    }

    [Fact]
    public void CompleteEmptyExecutionLedger_ImportsNothing()
    {
        var result = IbkrOfflineExecutionBaselinePlanner.Plan(
            [], Evidence(0m, []));
        Assert.Empty(result);
    }

    [Fact]
    public void IdenticalCompleteEvidenceAlreadyPersisted_IsIdempotent()
    {
        var existing = new IbkrOfflineJournalExecution("exec-1", Broker, 2m);
        Assert.Empty(IbkrOfflineExecutionBaselinePlanner.Plan(
            [existing], Evidence(2m, [Execution("exec-1", 2m)])));
    }

    [Fact]
    public void MissingPreviouslyPersistedExecution_FailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() =>
            IbkrOfflineExecutionBaselinePlanner.Plan(
                [new IbkrOfflineJournalExecution("exec-old", Broker, 2m)],
                Evidence(2m, [Execution("exec-new", 2m)])));
    }

    [Fact]
    public void PreviouslyPersistedExecutionQuantityOrBrokerMustRemainIdentical()
    {
        Assert.Throws<InvalidOperationException>(() =>
            IbkrOfflineExecutionBaselinePlanner.Plan(
                [new IbkrOfflineJournalExecution("exec-1", Broker, 1m)],
                Evidence(2m, [Execution("exec-1", 2m)])));

        Assert.Throws<InvalidOperationException>(() =>
            IbkrOfflineExecutionBaselinePlanner.Plan(
                [new IbkrOfflineJournalExecution("exec-1", "wrong-broker", 2m)],
                Evidence(2m, [Execution("exec-1", 2m)])));
    }

    [Fact]
    public void ConflictingDuplicateExecutionId_DoesNotGenerateBaseline()
    {
        Assert.Throws<InvalidOperationException>(() =>
            IbkrOfflineExecutionBaselinePlanner.Plan(
                [], Evidence(5m,
                    [Execution("exec-1", 2m), Execution("exec-1", 3m)])));
    }

    [Theory]
    [InlineData("account")]
    [InlineData("client")]
    [InlineData("broker")]
    public void WrongExecutionIdentity_IsNotAccepted(string dimension)
    {
        var source = Execution("exec-1", 2m);
        var modified = dimension switch
        {
            "account" => source with { AccountKey = "different-account" },
            "client" => source with { ClientOrderId = "different-coid" },
            _ => source with { BrokerOrderId = "different-broker" }
        };
        Assert.Throws<InvalidOperationException>(() =>
            IbkrOfflineExecutionBaselinePlanner.Plan(
                [], Evidence(2m, [modified])));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void NonpositiveExecutionAmount_FailsClosed(int amount)
    {
        Assert.Throws<InvalidOperationException>(() =>
            IbkrOfflineExecutionBaselinePlanner.Plan(
                [], Evidence(2m, [Execution("exec-1", amount)])));
    }

    [Fact]
    public void NonrepresentablePostgresPrecision_FailsBeforeDatabaseWrite()
    {
        Assert.Throws<InvalidOperationException>(() =>
            IbkrOfflineExecutionBaselinePlanner.Plan(
                [], Evidence(0.0000000000001m,
                    [Execution("exec-1", 0.0000000000001m)])));
        Assert.Throws<InvalidOperationException>(() =>
            IbkrOfflineExecutionBaselinePlanner.Plan(
                [], Evidence(10_000_000_000_000_000m,
                    [Execution("exec-1", 10_000_000_000_000_000m)])));
    }

    [Fact]
    public void CompleteLedgerSumMustEqualCumulativeFill()
    {
        Assert.Throws<InvalidOperationException>(() =>
            IbkrOfflineExecutionBaselinePlanner.Plan(
                [], Evidence(5m, [Execution("exec-1", 4m)])));
        Assert.Throws<InvalidOperationException>(() =>
            IbkrOfflineExecutionBaselinePlanner.Plan(
                [], Evidence(1m, [Execution("exec-1", 2m)])));
    }

    [Fact]
    public void DuplicatePersistedIds_AreCorruptionNotTwoFills()
    {
        var entry = new IbkrOfflineJournalExecution("exec-1", Broker, 2m);
        Assert.Throws<InvalidOperationException>(() =>
            IbkrOfflineExecutionBaselinePlanner.Plan(
                [entry, entry], Evidence(2m, [Execution("exec-1", 2m)])));
    }

    [Fact]
    public void IncompleteOrMissingExecutionPages_CannotBeImported()
    {
        var valid = Evidence(2m, [Execution("exec-1", 2m)]);
        Assert.Throws<InvalidOperationException>(() =>
            IbkrOfflineExecutionBaselinePlanner.Plan(
                [], valid with { ExecutionsComplete = false }));
        Assert.Throws<InvalidOperationException>(() =>
            IbkrOfflineExecutionBaselinePlanner.Plan(
                [], valid with { Executions = null! }));
    }

    [Fact]
    public void CapsExecutionEntries_ToAvoidUnboundedMemoryInput()
    {
        var many = Enumerable.Range(0, 4097)
            .Select(i => Execution("exec-" + i, 1m))
            .ToArray();
        Assert.Throws<InvalidOperationException>(() =>
            IbkrOfflineExecutionBaselinePlanner.Plan(
                [], Evidence(4097m, many)));
    }

    [Fact]
    public void NonmatchingBrokerIdentityOrInvalidScope_FailsClosed()
    {
        var good = Evidence(2m, [Execution("exec-1", 2m)]);
        Assert.Throws<ArgumentException>(() =>
            IbkrOfflineExecutionBaselinePlanner.Plan(
                [], good with { ClientOrderId = "../cross-scope" }));
        Assert.Throws<ArgumentException>(() =>
            IbkrOfflineExecutionBaselinePlanner.Plan(
                [], good with { AccountKey = " " }));
        Assert.Throws<ArgumentException>(() =>
            IbkrOfflineExecutionBaselinePlanner.Plan(
                [], good with { BrokerOrderId = "broker with spaces" }));
    }

    private static IbkrOfflineExecutionEvidence Execution(
        string executionId, decimal quantity) =>
        new(executionId, Account, Coid, Broker, quantity);

    private static IbkrOfflineBrokerOrderEvidence Evidence(
        decimal filled,
        IReadOnlyList<IbkrOfflineExecutionEvidence> executions) =>
        new(Account, Coid, Broker, "AAPL", OrderSide.Buy,
            filled > 5m ? filled : 5m,
            filled,
            IbkrOfflineBrokerOrderStatus.PartiallyFilled,
            ExecutionsComplete: true, Executions: executions);
}
