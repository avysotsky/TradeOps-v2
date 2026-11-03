using Microsoft.EntityFrameworkCore;
using TradeOps.Infrastructure.Exchange.Deribit;
using TradeOps.Infrastructure.Persistence;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DeribitSyntheticExecutionJournalTests
{
    private static DeribitSyntheticExecution Sample() =>
        new("synthetic-account-A", "synthetic-exec-001",
            "synthetic-order-001", "BTC-PERPETUAL", "buy",
            100m, 65000m, -0.00001m, "BTC",
            1791000000000L, null);

    [Fact]
    public void SyntheticInput_PreservesNativeAmountAndSignedFee()
    {
        var item = Sample();
        DeribitSyntheticExecutionJournal.Validate(item);
        Assert.Equal(100m, item.NativeAmount);
        Assert.Equal(-0.00001m, item.Fee);
        Assert.Null(item.Contracts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("account-A")]
    [InlineData("synthetic-account- A")]
    [InlineData("synthetic-account-A\n")]
    public void AccountScope_RejectsUntrustedOrRealKeys(string value)
    {
        Assert.Throws<InvalidOperationException>(() =>
            DeribitSyntheticExecutionJournal.Validate(
                Sample() with { AccountKey = value }));
    }

    [Fact]
    public void TradePayload_RejectsMalformedOrAmbiguousData()
    {
        var x = Sample();
        var invalid = new[]
        {
            x with { ExecutionId = "" },
            x with { ExecutionId = new string('a', 161) },
            x with { BrokerOrderId = "" },
            x with { InstrumentName = "\nBTC-PERPETUAL" },
            x with { Direction = "zero" },
            x with { NativeAmount = 0m },
            x with { Price = -1m },
            x with { FeeCurrency = " " },
            x with { TimestampMs = 0 },
            x with { Contracts = -1m }
        };
        foreach (var item in invalid)
        {
            Assert.Throws<InvalidOperationException>(() =>
                DeribitSyntheticExecutionJournal.Validate(item));
        }
    }

    [Theory]
    [InlineData("Host=deribit.com;Database=tradeops_api_smoke_a;Username=u;Password=p")]
    [InlineData("Host=127.0.0.1;Database=tradeops;Username=u;Password=p")]
    [InlineData("Host=127.0.0.1;Database=production;Username=u;Password=p")]
    [InlineData("Host=127.0.0.1;Database=tradeops_api_smoke_a;Search Path=public;Username=u;Password=p")]
    [InlineData("Host=127.0.0.1;Database=tradeops_api_smoke_a;Options=-c statement_timeout=3000;Username=u;Password=p")]
    public void ConnectionGuard_RejectsNonDisposableTargetsWithoutNetwork(string cs)
    {
        Assert.Throws<InvalidOperationException>(
            () => new DeribitSyntheticExecutionJournal(cs));
    }

    [Fact]
    public void ConnectionGuard_DisposableLoopbackAcceptedWithoutConnecting()
    {
        var journal = new DeribitSyntheticExecutionJournal(
            "Host=127.0.0.1;Port=5432;Database=tradeops_api_smoke_deribit;Username=u;Password=p");
        Assert.NotNull(journal);
    }

    [Fact]
    public void ExistingLiveFillModel_RemainsUnchanged()
    {
        using var db = new TradeOpsDbContext(
            new DbContextOptionsBuilder<TradeOpsDbContext>().UseNpgsql(
                "Host=127.0.0.1;Port=1;Database=model_only;Username=x;Password=x").Options);
        Assert.Null(db.Model.FindEntityType(typeof(DeribitSyntheticExecution)));
        var liveFill = db.Model.FindEntityType(typeof(TradeOps.Domain.Entities.Fill));
        Assert.NotNull(liveFill);
        Assert.Contains(liveFill!.GetIndexes(), x => x.IsUnique
            && x.Properties.Single().Name == "ExchangeFillId");
    }
}
