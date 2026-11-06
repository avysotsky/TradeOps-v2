using TradeOps.Application.Oms;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class OmsRiskLimitsTests
{
    [Theory]
    [InlineData("order")] [InlineData("delta")] [InlineData("portfolio")] [InlineData("leverage")]
    [InlineData("margin")] [InlineData("concentration")] [InlineData("loss")]
    [InlineData("units")] [InlineData("account")] [InlineData("instrument")]
    [InlineData("stale")] [InlineData("future")] [InlineData("binding")]
    [InlineData("reduce")] [InlineData("pending")] [InlineData("count")]
    [InlineData("overflow")] [InlineData("pending-margin")]
    public void InvalidOrExceededRiskCannotPass(string condition)
    {
        var command = DeribitCommandReceiptFixtures.Command(); var now = DateTimeOffset.UtcNow;
        var input = new OmsRiskInputsV1(new(1, command.Scope, "explicit", 2, 2, 10, 2, 10, 10, 5, 2, 2),
            new(1, command.Scope, "state", Convert.ToHexString(OmsJournalCanonical.EncodeCommand(command).Sha256),
                now.AddSeconds(-1), now.AddSeconds(1), true, true, true, false, 1, 1, 0, 0, 100, 0, 1, 0));
        Assert.True(OmsRiskLimitsV1.Allows(command, "state", input, 0, 0, 0, now));
        input = condition switch {
            "order" => input with { Policy = input.Policy with { MaxOrderUsd = .5m } },
            "delta" => input with { Valuation = input.Valuation with { PositionDeltaUsd = 3 } },
            "portfolio" => input with { Valuation = input.Valuation with { PortfolioGrossUsd = 10 } },
            "leverage" => input with { Valuation = input.Valuation with { EquityUsd = .1m } },
            "margin" => input with { Valuation = input.Valuation with { MarginUsedUsd = 10 } },
            "concentration" => input with { Valuation = input.Valuation with { InstrumentGrossUsd = 10 } },
            "loss" => input with { Valuation = input.Valuation with { DailyLossUsd = 5 } },
            "units" => input with { Valuation = input.Valuation with { UnitsVerified = false } },
            "account" => input with { Valuation = input.Valuation with { AccountEligible = false } },
            "instrument" => input with { Valuation = input.Valuation with { InstrumentEligible = false } },
            "stale" => input with { Valuation = input.Valuation with { ValidThroughUtc = now.AddSeconds(-1) } },
            "future" => input with { Valuation = input.Valuation with { ObservedAtUtc = now.AddSeconds(1) } },
            "binding" => input with { Valuation = input.Valuation with { CommandSha256 = "wrong" } },
            "reduce" => input with { Valuation = input.Valuation with { ReduceOnlyVerified = false } },
            "overflow" => input with { Valuation = input.Valuation with { PortfolioGrossUsd = decimal.MaxValue } },
            _ => input
        };
        if (condition == "reduce")
        {
            command = command with { Payload = command.Payload with { ReduceOnly = true } };
            input = input with { Valuation = input.Valuation with {
                CommandSha256 = Convert.ToHexString(OmsJournalCanonical.EncodeCommand(command).Sha256) } };
        }
        Assert.False(OmsRiskLimitsV1.Allows(command, "state", input, condition == "pending" ? 2 : 0,
            condition == "pending-margin" ? 10 : 0, condition == "count" ? 2 : 0, now));
    }
}
