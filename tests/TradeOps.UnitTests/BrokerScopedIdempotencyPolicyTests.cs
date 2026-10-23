using TradeOps.Application.Models;
using TradeOps.Domain.Enums;
using Xunit;

namespace TradeOps.UnitTests;

/// <summary>
/// Pure synthetic cross-provider collision classification. A "Distinct"
/// result is NOT a database insert or broker order submission permission.
/// </summary>
public sealed class BrokerScopedIdempotencyPolicyTests
{
    private static readonly Guid OrderId = Guid.Parse(
        "00000000-0000-4000-a000-000000000101");
    private static readonly Guid AnotherOrderId = Guid.Parse(
        "00000000-0000-4000-a000-000000000102");

    [Fact]
    public void ExactOrderReplay_IsIdenticalOnlyForAllImmutableFields()
    {
        var local = Order();

        var result = BrokerScopedIdempotencyPolicy.CompareOrders(local, local with { });

        Assert.Equal(ScopedIdempotencyDisposition.EquivalentReplay,
            result.Disposition);
    }

    [Fact]
    public void SameClientIdAcrossDifferentAccounts_IsDistinctNotGloballyDuplicate()
    {
        var first = Order();
        var second = first with
        {
            AccountKey = "synthetic-account-B",
            LocalOrderId = AnotherOrderId
        };

        Assert.Equal(ScopedIdempotencyDisposition.DistinctScopedIdentity,
            BrokerScopedIdempotencyPolicy.CompareOrders(first, second).Disposition);
    }

    [Fact]
    public void SameClientIdAcrossDifferentProviders_IsDistinctNotGloballyDuplicate()
    {
        var first = Order();
        var second = first with
        {
            ProviderKey = "IbkrOffline",
            LocalOrderId = AnotherOrderId,
            InstrumentKey = "SYNTHETIC-IBKR-SYMBOL:BTCUSDT"
        };

        Assert.Equal(ScopedIdempotencyDisposition.DistinctScopedIdentity,
            BrokerScopedIdempotencyPolicy.CompareOrders(first, second).Disposition);
    }

    [Fact]
    public void ReusingLocalGuidAcrossAccountsOrBrokers_IsAlwaysConflict()
    {
        var first = Order();
        var otherAccount = first with { AccountKey = "another-account" };
        var otherBroker = first with
        {
            ProviderKey = "IbkrOffline",
            InstrumentKey = "SYNTHETIC-IBKR-SYMBOL:BTCUSDT"
        };

        Assert.Equal(ScopedIdempotencyDisposition.IdentityConflict,
            BrokerScopedIdempotencyPolicy.CompareOrders(first, otherAccount).Disposition);
        Assert.Equal(ScopedIdempotencyDisposition.IdentityConflict,
            BrokerScopedIdempotencyPolicy.CompareOrders(first, otherBroker).Disposition);
    }

    [Fact]
    public void ClientIdBoundToAnotherLocalOrder_IsConflict()
    {
        Assert.Equal(ScopedIdempotencyDisposition.IdentityConflict,
            BrokerScopedIdempotencyPolicy.CompareOrders(
                Order(), Order() with { LocalOrderId = AnotherOrderId }).Disposition);
    }

    [Fact]
    public void BrokerOrderIdBoundToAnotherClientId_IsConflict()
    {
        Assert.Equal(ScopedIdempotencyDisposition.IdentityConflict,
            BrokerScopedIdempotencyPolicy.CompareOrders(
                Order(),
                Order() with
                {
                    ClientOrderId = "different-coid",
                    LocalOrderId = AnotherOrderId
                }).Disposition);
    }

    [Fact]
    public void OneLocalGuidCannotBecomeAnotherClientOrderId()
    {
        Assert.Equal(ScopedIdempotencyDisposition.IdentityConflict,
            BrokerScopedIdempotencyPolicy.CompareOrders(
                Order(),
                Order() with
                {
                    ClientOrderId = "different-client",
                    BrokerOrderId = "different-broker"
                }).Disposition);
    }

    [Fact]
    public void DifferentScopedOrderIdsAreDistinct_NotPermissionToSubmit()
    {
        var second = Order() with
        {
            LocalOrderId = AnotherOrderId,
            ClientOrderId = "different-coid",
            BrokerOrderId = "different-broker"
        };

        var decision = BrokerScopedIdempotencyPolicy.CompareOrders(Order(), second);

        Assert.Equal(ScopedIdempotencyDisposition.DistinctScopedIdentity,
            decision.Disposition);
        Assert.Null(typeof(ScopedIdempotencyAssessment)
            .GetMethod("SubmitOrder"));
    }

    [Theory]
    [InlineData("InstrumentKey")]
    [InlineData("Side")]
    [InlineData("OrderType")]
    [InlineData("RequestedQuantity")]
    [InlineData("Price")]
    public void ChangingSubmittedImmutableOrderTerms_IsConflict(string field)
    {
        var original = Order();
        var other = field switch
        {
            "InstrumentKey" => original with { InstrumentKey = "MOCK:ETHUSDT" },
            "Side" => original with { Side = OrderSide.Sell },
            "OrderType" => original with { OrderType = OrderType.Market },
            "RequestedQuantity" => original with { RequestedQuantity = 6m },
            "Price" => original with { Price = 123.5m },
            _ => throw new InvalidOperationException("Test has an unknown field.")
        };

        Assert.Equal(ScopedIdempotencyDisposition.IdentityConflict,
            BrokerScopedIdempotencyPolicy.CompareOrders(original, other).Disposition);
    }

    [Fact]
    public void FirstBrokerIdBinding_RequiresSeparateAuthenticatedProof()
    {
        var unbound = Order() with { BrokerOrderId = null };
        var bound = Order();

        var decision = BrokerScopedIdempotencyPolicy.CompareOrders(
            unbound, bound);
        Assert.Equal(
            ScopedIdempotencyDisposition.BrokerBindingRequiresReview,
            decision.Disposition);

        Assert.Equal(ScopedIdempotencyDisposition.IdentityConflict,
            BrokerScopedIdempotencyPolicy.CompareOrders(bound, unbound).Disposition);
        Assert.Equal(ScopedIdempotencyDisposition.IdentityConflict,
            BrokerScopedIdempotencyPolicy.CompareOrders(
                bound, bound with { BrokerOrderId = "different-broker" }).Disposition);
    }

    [Fact]
    public void TwoIdenticalUnboundOrders_HaveEquivalentScopedFingerprint()
    {
        var item = Order() with { BrokerOrderId = null };
        Assert.Equal(ScopedIdempotencyDisposition.EquivalentReplay,
            BrokerScopedIdempotencyPolicy.CompareOrders(item, item with { })
                .Disposition);
    }

    [Fact]
    public void SameExecutionIdInDifferentAccounts_IsDistinct()
    {
        var first = Execution();
        var second = first with
        {
            AccountKey = "synthetic-account-B",
            LocalOrderId = AnotherOrderId
        };

        Assert.Equal(ScopedIdempotencyDisposition.DistinctScopedIdentity,
            BrokerScopedIdempotencyPolicy.CompareExecutions(first, second).Disposition);
    }

    [Fact]
    public void SameExecutionIdInDifferentProviders_IsDistinct()
    {
        var first = Execution();
        var second = first with
        {
            ProviderKey = "IbkrOffline",
            LocalOrderId = AnotherOrderId
        };

        Assert.Equal(ScopedIdempotencyDisposition.DistinctScopedIdentity,
            BrokerScopedIdempotencyPolicy.CompareExecutions(first, second).Disposition);
    }

    [Fact]
    public void IdenticalExecutionIdAndImmutablePayload_IsReplay()
    {
        var first = Execution();
        var result = BrokerScopedIdempotencyPolicy.CompareExecutions(
            first, first with { });

        Assert.Equal(ScopedIdempotencyDisposition.EquivalentReplay,
            result.Disposition);
    }

    [Fact]
    public void ExecutionIdReboundToAnotherOrderWithinAccount_IsConflict()
    {
        var first = Execution();
        var second = first with
        {
            LocalOrderId = AnotherOrderId,
            ClientOrderId = "other-coid",
            BrokerOrderId = "other-broker"
        };

        Assert.Equal(ScopedIdempotencyDisposition.IdentityConflict,
            BrokerScopedIdempotencyPolicy.CompareExecutions(first, second).Disposition);
    }

    [Theory]
    [InlineData("Quantity")]
    [InlineData("Price")]
    [InlineData("Fee")]
    [InlineData("FeeCurrency")]
    [InlineData("ClientOrderId")]
    [InlineData("BrokerOrderId")]
    public void ExecutionImmutableDataConflict_CannotBeCalledReplay(string field)
    {
        var first = Execution();
        var second = field switch
        {
            "Quantity" => first with { Quantity = 3m },
            "Price" => first with { Price = 100.1m },
            "Fee" => first with { Fee = 0.7m },
            "FeeCurrency" => first with { FeeCurrency = "USDT" },
            "ClientOrderId" => first with { ClientOrderId = "other-coid" },
            "BrokerOrderId" => first with { BrokerOrderId = "other-broker" },
            _ => throw new InvalidOperationException("Unknown test case.")
        };

        Assert.Equal(ScopedIdempotencyDisposition.IdentityConflict,
            BrokerScopedIdempotencyPolicy.CompareExecutions(first, second).Disposition);
    }

    [Fact]
    public void TwoDifferentExecutionIdsInsideSameOrderAreDistinct()
    {
        Assert.Equal(ScopedIdempotencyDisposition.DistinctScopedIdentity,
            BrokerScopedIdempotencyPolicy.CompareExecutions(
                Execution(), Execution() with { ExecutionId = "fill-id-2" })
                .Disposition);
    }

    [Fact]
    public void ALocalOrderGuidMayNotBeReboundAcrossExecutionAccountScopes()
    {
        var old = Execution();
        var other = old with { AccountKey = "other-account" };

        Assert.Equal(ScopedIdempotencyDisposition.IdentityConflict,
            BrokerScopedIdempotencyPolicy.CompareExecutions(old, other).Disposition);
    }

    [Theory]
    [InlineData("IbkrPaper")]
    [InlineData("Bybit")]
    [InlineData("mock")]
    [InlineData("")]
    public void UnknownProviderCannotBePromotedToSyntheticTrustedScope(
        string provider)
    {
        Assert.Throws<InvalidOperationException>(() =>
            BrokerScopedIdempotencyPolicy.CompareOrders(
                Order() with { ProviderKey = provider }, Order()));
        Assert.Throws<InvalidOperationException>(() =>
            BrokerScopedIdempotencyPolicy.CompareExecutions(
                Execution() with { ProviderKey = provider }, Execution()));
    }

    [Fact]
    public void UnqualifiedIbkrInstrumentAndMalformedKeysFailClosed()
    {
        var valid = Order() with
        {
            ProviderKey = "IbkrOffline",
            InstrumentKey = "SYNTHETIC-IBKR-SYMBOL:BTCUSDT"
        };
        Assert.Equal(ScopedIdempotencyDisposition.EquivalentReplay,
            BrokerScopedIdempotencyPolicy.CompareOrders(valid, valid with { })
                .Disposition);

        foreach (var bad in new[]
        {
            valid with { InstrumentKey = "BTCUSDT" },
            valid with { InstrumentKey = "SYNTHETIC-IBKR-SYMBOL:" },
            valid with { AccountKey = " " },
            valid with { AccountKey = " bad" },
            valid with { AccountKey = "bad\naccount" },
            valid with { ClientOrderId = "" },
            valid with { ClientOrderId = new string('X', 101) },
            valid with { BrokerOrderId = " " },
            valid with { LocalOrderId = Guid.Empty }
        })
        {
            Assert.Throws<InvalidOperationException>(() =>
                BrokerScopedIdempotencyPolicy.CompareOrders(valid, bad));
        }
    }

    [Fact]
    public void InvalidOrderOrExecutionAmountNeverBecomesReplay()
    {
        var original = Order();
        foreach (var bad in new[]
        {
            original with { RequestedQuantity = 0m },
            original with { RequestedQuantity = -2m },
            original with { RequestedQuantity = 0.0000000000001m },
            original with { Price = 0m },
            original with { Price = -1m },
            original with { Side = (OrderSide)999 },
            original with { OrderType = (OrderType)999 }
        })
        {
            Assert.Throws<InvalidOperationException>(() =>
                BrokerScopedIdempotencyPolicy.CompareOrders(original, bad));
        }

        var fill = Execution();
        foreach (var bad in new[]
        {
            fill with { Quantity = 0m },
            fill with { Quantity = -1m },
            fill with { Quantity = 0.0000000000001m },
            fill with { Price = -1m },
            fill with { Price = 0m },
            fill with { Fee = -0.1m },
            fill with { ExecutionId = " " },
            fill with { FeeCurrency = new string('X', 21) }
        })
        {
            Assert.Throws<InvalidOperationException>(() =>
                BrokerScopedIdempotencyPolicy.CompareExecutions(fill, bad));
        }
    }

    [Fact]
    public void PolicyHasNoDatabaseOrBrokerDependencies()
    {
        var policyAssembly = typeof(BrokerScopedIdempotencyPolicy).Assembly;
        Assert.Contains("TradeOps.Application", policyAssembly.GetName().Name!);
        Assert.DoesNotContain(
            typeof(BrokerScopedIdempotencyPolicy).GetMethods(),
            m => m.Name.Contains("PlaceOrder", StringComparison.Ordinal)
                || m.Name.Contains("CancelOrder", StringComparison.Ordinal)
                || m.Name.Contains("SaveChanges", StringComparison.Ordinal));
    }

    private static ScopedOrderFingerprint Order() =>
        new("Mock", "synthetic-account-A", OrderId,
            "client-order-1", "broker-order-1", "MOCK:BTCUSDT",
            OrderSide.Buy, OrderType.Limit, 5m, 100m);

    private static ScopedExecutionFingerprint Execution() =>
        new("Mock", "synthetic-account-A", OrderId,
            "client-order-1", "broker-order-1", "fill-id-1",
            2m, 100m, 0.01m, "USD");
}
