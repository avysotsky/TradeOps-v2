using System;
using TradeOps.Domain.Oms;
using TradeOps.Domain.Enums;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class OmsIdentityContractTests
{
    private static AccountKey Scope(string venue = "Deribit", string account = "main",
        OmsEnvironment environment = OmsEnvironment.Testnet, string tenant = "firm") =>
        new(new TenantId(tenant), new VenueId(venue), environment, new TradingAccountId(account));

    [Fact]
    public void ClientOrderLabels_AreUniqueOnlyWithinAccountScope()
    {
        var first = new ScopedClientOrderId(Scope(account: "trader-A"), new ClientOrderLabel("same-label"));
        var second = new ScopedClientOrderId(Scope(account: "trader-B"), new ClientOrderLabel("same-label"));
        var alias = new ScopedClientOrderId(Scope(venue: "DERIBIT", account: "trader-A"),
            new ClientOrderLabel("same-label"));
        Assert.NotEqual(first, second);
        Assert.Equal(first, alias); // venue registry names canonicalize, remote identifiers never do
        Assert.Equal(2, new[] { first, second, alias }.Distinct().Count());
    }

    [Fact]
    public void ExchangeOrderIds_CanOverlapAcrossVenuesAndEnvironments()
    {
        var a = new ScopedExchangeOrderId(Scope(), new ExchangeOrderKey("123"));
        var b = new ScopedExchangeOrderId(Scope(venue: "CoinCall"), new ExchangeOrderKey("123"));
        var c = new ScopedExchangeOrderId(Scope(environment: OmsEnvironment.Mainnet),
            new ExchangeOrderKey("123"));
        Assert.Equal(3, new[] { a, b, c }.Distinct().Count());
    }

    [Fact]
    public void ExecutionIds_AreScopedToAccountVenueAndAdapterNamespace()
    {
        var a = new ScopedExecutionId(Scope(), new ExecutionNamespace("FIX:sender->target:session-1"),
            new ExecutionKey("123"));
        var b = a with { Namespace = new ExecutionNamespace("FIX:sender->target:session-2") };
        var c = new ScopedExecutionId(Scope(account: "alt"), a.Namespace, a.ExecutionId);
        var d = new ScopedExecutionId(Scope(venue: "CoinCall"), a.Namespace, a.ExecutionId);
        Assert.Equal(4, new[] { a, b, c, d }.Distinct().Count());
    }

    [Fact]
    public void OpaqueIds_PreserveCaseAndRejectWhitespaceOrDefaultKeys()
    {
        Assert.NotEqual(new ClientOrderLabel("AbC"), new ClientOrderLabel("abc"));
        Assert.Throws<ArgumentException>(() => new ClientOrderLabel(" ID "));
        Assert.Throws<ArgumentException>(() => new ExchangeOrderKey(""));
        Assert.Throws<ArgumentException>(() => new OrderIntentId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new AccountKey(default, new VenueId("Deribit"),
            OmsEnvironment.Testnet, new TradingAccountId("main")));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DispatchAttempt(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OrderVersion(-1));
    }

    [Fact]
    public void ExchangeDiscoveredOrder_HasNoSyntheticIntent()
    {
        var identity = new OmsOrderIdentity(Scope(), new OmsLocalOrderId(Guid.NewGuid()),
            intentId: null, clientLabel: null, exchangeId: new ExchangeOrderKey("remote-only"));
        Assert.Null(identity.IntentId);
        Assert.NotNull(identity.ExchangeId);

        Assert.Throws<ArgumentException>(() => new OmsOrderIdentity(Scope(),
            new OmsLocalOrderId(Guid.NewGuid()), null, null, null));
    }

    [Fact]
    public void FixClOrdId_And_OrigClOrdId_AreDifferentTypedRoles()
    {
        var replace = new FixOrderCorrelation(new FixClOrdId("replace-2"),
            new FixOrigClOrdId("new-1")).Validate();
        Assert.Equal("replace-2", replace.ClOrdId.Value);
        Assert.Equal("new-1", replace.OrigClOrdId!.Value.Value);
    }

    [Fact]
    public void ExecutionDeduplication_ReplayIsNotASecondFill_ButPayloadConflictIs()
    {
        var scope = Scope();
        var local = new OmsLocalOrderId(Guid.Parse("f0f00000-0000-4000-8000-000000000001"));
        var id = new OmsExecutionIdentity(
            new ScopedExecutionId(scope, new ExecutionNamespace("Deribit:trade-id"),
                new ExecutionKey("trade-42")), local);
        var time = new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero);
        var a = new OmsExecution(id, 2m, OmsQuantityUnit.Contracts, 60000m, time);
        var replay = new OmsExecution(id, 2m, OmsQuantityUnit.Contracts, 60000m, time);
        var conflict = new OmsExecution(id, 3m, OmsQuantityUnit.Contracts, 60000m, time);
        Assert.Equal(OmsExecutionDeduplication.New, OmsExecutionContract.Compare(null, a));
        Assert.Equal(OmsExecutionDeduplication.Duplicate, OmsExecutionContract.Compare(a, replay));
        Assert.Equal(OmsExecutionDeduplication.Collision, OmsExecutionContract.Compare(a, conflict));

        var otherScope = new OmsExecution(new OmsExecutionIdentity(
            new ScopedExecutionId(Scope(account: "other"), id.ScopedId.Namespace,
                id.ScopedId.ExecutionId), local), 2m, OmsQuantityUnit.Contracts, 60000m, time);
        Assert.Equal(OmsExecutionDeduplication.New, OmsExecutionContract.Compare(a, otherScope));
    }

    [Fact]
    public void ImmutableTerms_RequireExplicitQuantityUnit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OmsOrderTerms("BTC-PERPETUAL", OrderSide.Buy, OrderType.Limit,
                0m, OmsQuantityUnit.Contracts));
        var terms = new OmsOrderTerms("BTC-PERPETUAL", OrderSide.Buy, OrderType.Limit,
            10m, OmsQuantityUnit.Contracts);
        Assert.Equal(10m, terms.RequestedQuantity);
        Assert.Equal(OmsQuantityUnit.Contracts, terms.QuantityUnit);
    }
}
