using System.Text.Json.Nodes;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;
using Xunit;
using static TradeOps.UnitTests.DeribitCommandReceiptFixtures;

namespace TradeOps.UnitTests;

public sealed class DeribitCommandReceiptContractTests
{
    [Fact]
    public void NewCanonicalV1_RoundTripsWithoutChangingOldJournalEncoding()
    {
        var receipt = Receipt(Command()); var before = OmsJournalCanonical.EncodeCommand(receipt.Command);
        var encoded = DeribitCommandReceiptCanonical.Encode(receipt);
        Assert.Equal(receipt, DeribitCommandReceiptCanonical.Decode(encoded.Json, receipt.Command));
        Assert.Equal(before.Json, OmsJournalCanonical.EncodeCommand(receipt.Command).Json);
        Assert.Equal(before.Sha256, OmsJournalCanonical.EncodeCommand(receipt.Command).Sha256);
        Assert.Equal(1, JsonNode.Parse(encoded.Json)!["schemaVersion"]!.GetValue<int>());
        var scaled = receipt with { Command = receipt.Command with { Payload = receipt.Command.Payload with {
            Quantity = 1.000m, LimitPrice = 2000.000m } }, Order = receipt.Order with { NativeAmount = 1.00m, Price = 2000.00m } };
        Assert.Equal(encoded.Json, DeribitCommandReceiptCanonical.Encode(scaled).Json);
        Assert.Equal(encoded.Sha256, DeribitCommandReceiptCanonical.Encode(scaled).Sha256);
    }

    [Theory]
    [InlineData("attempt")] [InlineData("session")] [InlineData("rpc")]
    [InlineData("method")] [InlineData("quantity")] [InlineData("units")]
    [InlineData("price")] [InlineData("side")] [InlineData("instrument")]
    [InlineData("cumulative")] [InlineData("filled-total")] [InlineData("post-only")]
    [InlineData("reduce-only")] [InlineData("before-send")] [InlineData("duration")]
    [InlineData("venue-future")] [InlineData("kind")] [InlineData("label")]
    [InlineData("cancel-target")] [InlineData("replace")] [InlineData("fix")] [InlineData("placement-version")]
    public void UnsupportedOrMismatchedReceipt_NeverGetsCanonicalEvidence(string mode)
    {
        var r = Receipt(Command());
        r = mode switch {
            "attempt" => r with { AttemptNumber = 2 }, "session" => r with { SessionId = Guid.Empty },
            "rpc" => r with { RpcRequestId = 0 }, "method" => r with { Method = "private/get_order_state" },
            "quantity" => r with { Order = r.Order with { NativeAmount = 2 } },
            "units" => r with { Command = r.Command with { Payload = r.Command.Payload with { QuantityUnit = OmsQuantityUnit.Contracts } } },
            "price" => r with { Order = r.Order with { Price = 1999 } },
            "side" => r with { Order = r.Order with { Side = TradeOps.Domain.Enums.OrderSide.Sell } },
            "instrument" => r with { Order = r.Order with { NativeInstrument = "foreign" } },
            "cumulative" => r with { Order = r.Order with { FilledNativeAmount = null } },
            "filled-total" => r with { Order = r.Order with { OrderState = "filled", FilledNativeAmount = 0 } },
            "post-only" => r with { PostOnly = false }, "reduce-only" => r with { ReduceOnly = true },
            "before-send" => r with { ReceivedAtUtc = r.SentAtUtc.AddSeconds(-1) },
            "duration" => r with { ReceivedAtUtc = r.SentAtUtc.AddMinutes(2) },
            "venue-future" => r with { Order = r.Order with { VenueTimestampUtc = r.ReceivedAtUtc.AddSeconds(6) } },
            "kind" => r with { InstrumentKind = "option" },
            "label" => r with { Command = r.Command with { Payload = r.Command.Payload with { TargetClientOrderId = "unmatched" } } },
            "cancel-target" => Receipt(Cancel(r.Command, "foreign")),
            "replace" => r with { Command = r.Command with { Payload = r.Command.Payload with { Kind = OmsJournalCommandKind.Replace, TargetExchangeOrderId = "native-1" } } },
            "placement-version" => r with { Command = r.Command with { ExpectedAggregateVersion = new(1) } },
            _ => r with { Command = r.Command with { Payload = r.Command.Payload with { FixClOrdId = "FIX-is-another-protocol" } } }
        };
        Assert.ThrowsAny<ArgumentException>(() => DeribitCommandReceiptCanonical.Encode(r));
    }

    [Theory]
    [InlineData("regression")] [InlineData("terminal-placement")] [InlineData("venue-time")]
    public void CancelCannotRegressOrCancelAnAlreadyTerminalPlacement(string mode)
    {
        var place = Receipt(Command(), sent: Now.AddSeconds(-21));
        var cancel = Receipt(Cancel(place.Command), rpcId: 2, sent: Now.AddSeconds(-20));
        switch (mode)
        {
            case "regression":
                place = place with { Order = place.Order with { FilledNativeAmount = 0.75m } };
                cancel = cancel with { Order = cancel.Order with { FilledNativeAmount = 0.5m } }; break;
            case "terminal-placement":
                place = place with { Order = place.Order with { OrderState = "filled", FilledNativeAmount = 1 } };
                cancel = cancel with { Order = cancel.Order with { FilledNativeAmount = 1 } }; break;
            default:
                cancel = cancel with { Order = cancel.Order with { VenueTimestampUtc = place.Order.VenueTimestampUtc.AddMilliseconds(-1) } }; break;
        }
        place.Validate(); cancel.Validate(); // Each response alone is well-formed; the pair is inconsistent.
        Assert.Throws<ArgumentException>(() => DeribitCommandReceiptSet.ValidateCancelBinding(cancel, place));
    }

    [Theory]
    [InlineData("schemaVersion")] [InlineData("extra")] [InlineData("command.account")]
    [InlineData("missingOptional")] [InlineData("order.extra")]
    public void DecodingCannotDropOrSubstituteDurableFields(string mode)
    {
        var r = Receipt(Command()); var node = JsonNode.Parse(DeribitCommandReceiptCanonical.Encode(r).Json)!;
        switch (mode)
        {
            case "schemaVersion": node["schemaVersion"] = 2; break;
            case "extra": node["extra"] = true; break;
            case "command.account": node["command"]!["account"] = "foreign"; break;
            case "missingOptional": node["order"]!.AsObject().Remove("clientLabel"); break;
            default: node["order"]!["extra"] = true; break;
        }
        Assert.ThrowsAny<Exception>(() => DeribitCommandReceiptCanonical.Decode(node.ToJsonString(), r.Command));
    }
}
