using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Domain.Enums;
using TradeOps.Domain.Oms;
using TradeOps.Infrastructure.Persistence.Oms;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class OmsPrivateEventContractTests
{
    internal static readonly DateTimeOffset Time = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    internal static AccountKey Scope(string tenant = "tenant", OmsEnvironment environment = OmsEnvironment.Testnet,
        string account = "account", string? sub = null) => new(new(tenant), new("Deribit"), environment,
            new(account), sub is null ? null : new TradingSubaccountId(sub));
    internal static OmsPrivateEventEnvelope Execution(AccountKey? scope = null) => new(scope ?? Scope(),
        new(OmsJournalEventKind.Execution, "BTC", "BTC-PERPETUAL", "order-1", OrderSide.Buy,
            10m, OmsPrivateAmountUnit.Usd, "USD", Time, ExchangeTradeId: "trade-1",
            PriceKind: OmsPrivatePriceKind.Numeric, Price: 65000m, Fee: -0.00001m, FeeCurrency: "BTC"),
        new(Guid.NewGuid(), Guid.NewGuid(), Time.AddSeconds(1), OmsPrivateCaptureSource.WebSocket,
            "user.trades.future.BTC.100ms"));
    internal static OmsPrivateEventEnvelope Order() => Execution() with
    {
        Facts = new(OmsJournalEventKind.Order, "BTC", "BTC-PERPETUAL", "order-1", OrderSide.Buy,
            100m, OmsPrivateAmountUnit.Usd, "USD", Time, OrderState: "open", FilledNativeAmount: 30m,
            PriceKind: OmsPrivatePriceKind.Numeric, Price: 65000m),
        Observation = new(Guid.NewGuid(), Guid.NewGuid(), Time.AddSeconds(1),
            OmsPrivateCaptureSource.WebSocket, "user.orders.future.BTC.raw")
    };

    [Fact]
    public void ReconnectReceiptEpochAndSource_DoNotChangeImmutableIdentityOrFacts()
    {
        var first = Execution();
        var replay = first with { Observation = new(Guid.NewGuid(), Guid.NewGuid(), Time.AddDays(1),
            OmsPrivateCaptureSource.RecoveryQuery, "private/get_user_trades_by_currency") };
        var a = OmsPrivateEventCanonical.Encode(first);
        var b = OmsPrivateEventCanonical.Encode(replay);
        Assert.Equal(a.Namespace, b.Namespace);
        Assert.Equal(a.EventId, b.EventId);
        Assert.Equal(a.Json, b.Json);
        Assert.Equal(a.Sha256, b.Sha256);
        Assert.NotEqual(OmsPrivateEventCanonical.EncodeObservation(first, a).Json,
            OmsPrivateEventCanonical.EncodeObservation(replay, b).Json);
    }

    [Fact]
    public void ExecutionIdentity_SeparatesCurrencyAndOwnOrderButDetectsChangedSideOrPrice()
    {
        var first = Execution();
        var original = OmsPrivateEventCanonical.Encode(first);
        var otherCurrency = OmsPrivateEventCanonical.Encode(first with { Facts = first.Facts with { Currency = "ETH" } });
        var otherLeg = OmsPrivateEventCanonical.Encode(first with { Facts = first.Facts with { ExchangeOrderId = "order-2" } });
        var changedSide = OmsPrivateEventCanonical.Encode(first with { Facts = first.Facts with { Side = OrderSide.Sell } });
        var changedPrice = OmsPrivateEventCanonical.Encode(first with { Facts = first.Facts with { Price = 65001m } });
        Assert.NotEqual(original.Namespace, otherCurrency.Namespace);
        Assert.NotEqual(original.EventId, otherLeg.EventId);
        Assert.Equal(original.EventId, changedSide.EventId);
        Assert.Equal(original.EventId, changedPrice.EventId);
        Assert.NotEqual(original.Json, changedSide.Json);
        Assert.NotEqual(original.Json, changedPrice.Json);
    }

    [Fact]
    public void DecimalScale_CanonicalizesBothExecutionFactsAndContentAddressedOrderReports()
    {
        foreach (var first in new[] { Execution(), Order() })
        {
            var replay = first with { Facts = first.Facts with
            { NativeAmount = first.Facts.NativeAmount * 1.000m, Price = first.Facts.Price * 1.000m,
              FilledNativeAmount = first.Facts.FilledNativeAmount * 1.000m, Fee = first.Facts.Fee * 1.000m } };
            var a = OmsPrivateEventCanonical.Encode(first);
            var b = OmsPrivateEventCanonical.Encode(replay);
            Assert.Equal(a.Json, b.Json);
            Assert.Equal(a.Sha256, b.Sha256);
            Assert.Equal(a.EventId, b.EventId);
        }
    }

    [Fact]
    public void OrderReportsAtSameTimestamp_PreserveDistinctFactsWithoutInventingRevisionOrFill()
    {
        var first = Order();
        var next = first with { Facts = first.Facts with { OrderState = "cancelled", FilledNativeAmount = 40m } };
        Assert.NotEqual(OmsPrivateEventCanonical.Encode(first).EventId, OmsPrivateEventCanonical.Encode(next).EventId);
        Assert.Null(first.Facts.ExchangeTradeId);
        Assert.Null(first.Facts.LeavesNativeAmount);
        Assert.Equal(first.Facts.VenueTimestampUtc, next.Facts.VenueTimestampUtc);
    }

    [Fact]
    public void NativeAmountSignedFeeAndDecimalPrecision_ArePreservedWithoutConversion()
    {
        var item = Execution() with { Facts = Execution().Facts with
        { NativeAmount = 0.1234567890123456789012345678m, AmountUnit = OmsPrivateAmountUnit.BaseCoin,
          AmountCurrency = "BTC", Contracts = null, Price = 0m } };
        using var json = JsonDocument.Parse(OmsPrivateEventCanonical.Encode(item).Json);
        var facts = json.RootElement.GetProperty("facts");
        Assert.Equal(item.Facts.NativeAmount, facts.GetProperty("nativeAmount").GetDecimal());
        Assert.Equal(-0.00001m, facts.GetProperty("fee").GetDecimal());
        Assert.Equal(JsonValueKind.Null, facts.GetProperty("contracts").ValueKind);
        Assert.Equal(0m, facts.GetProperty("price").GetDecimal());
    }

    [Fact]
    public void FullAccountScope_IsEncodedInEveryImmutableEvent()
    {
        var item = Execution();
        var variants = new[] { Scope(), Scope(tenant: "other"), Scope(environment: OmsEnvironment.Mainnet),
            Scope(account: "other"), Scope(sub: "desk-1"), Scope(sub: "desk-2") };
        Assert.Equal(variants.Length, variants.Select(x => OmsPrivateEventCanonical.Encode(item with { Scope = x }).Json).Distinct().Count());
    }

    [Theory]
    [InlineData("trade-id")]
    [InlineData("timestamp")]
    [InlineData("amount")]
    [InlineData("unit")]
    [InlineData("price")]
    [InlineData("fee")]
    [InlineData("currency")]
    [InlineData("capture-id")]
    [InlineData("epoch")]
    [InlineData("venue")]
    public async Task InvalidCapture_IsRejectedBeforeDatabaseAccess(string defect)
    {
        var item = Execution();
        item = defect switch
        {
            "trade-id" => item with { Facts = item.Facts with { ExchangeTradeId = null } },
            "timestamp" => item with { Facts = item.Facts with { VenueTimestampUtc = default } },
            "amount" => item with { Facts = item.Facts with { NativeAmount = 0 } },
            "unit" => item with { Facts = item.Facts with { AmountCurrency = "BTC" } },
            "price" => item with { Facts = item.Facts with { Price = null } },
            "fee" => item with { Facts = item.Facts with { FeeCurrency = null } },
            "currency" => item with { Facts = item.Facts with { Currency = "any" } },
            "capture-id" => item with { Observation = item.Observation with { CaptureId = Guid.Empty } },
            "epoch" => item with { Observation = item.Observation with { StreamEpoch = Guid.Empty } },
            _ => item with { Scope = new(new("tenant"), new("CoinCall"), OmsEnvironment.Testnet, new("account")) }
        };
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            new OmsPostgresPrivateEventCapture(null!).CaptureAsync(item));
    }

    [Fact]
    public void V1InboxBytesAndHash_StayFrozenIncludingOriginalDecimalScale()
    {
        var item = new OmsJournalInboxEvent(Scope(), "Deribit:trade-id", "trade-v1",
            OmsJournalEventKind.Execution, null, "BTC-PERPETUAL", 1.000m, 100.00m, Time);
        const string expected = """
{"schemaVersion":1,"tenant":"tenant","venue":"DERIBIT","environment":3,"account":"account","subaccount":"","eventNamespace":"Deribit:trade-id","eventId":"trade-v1","kind":"Execution","aggregateId":null,"instrument":"BTC-PERPETUAL","quantity":1.000,"price":100.00,"occurredAtUtc":"2026-11-04T12:00:00+00:00"}
""";
        var encoded = OmsJournalCanonical.EncodeInbox(item);
        Assert.Equal(expected, encoded.Json);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(expected)), encoded.Sha256);
        Assert.Equal(1, OmsContractSchema.Version);
    }
}
