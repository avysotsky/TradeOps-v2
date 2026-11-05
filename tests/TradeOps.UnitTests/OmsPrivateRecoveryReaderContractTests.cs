using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;
using Xunit;
using static TradeOps.UnitTests.DeribitOrderRecoveryProjectorTests;

namespace TradeOps.UnitTests;

public sealed class OmsPrivateRecoveryReaderContractTests
{
    private static OmsPrivateRecoveryReadResult Evidence() => new(Account, Guid.NewGuid(), Through, 3,
        new[] { Prior(), Fill(), Report(filled: 4) }
            .Select(x => new OmsPrivateRecoveryObservation(x, OmsPrivateCaptureResult.Inserted)).ToArray(), []);

    [Theory]
    [InlineData(OmsPrivateRecoveryReadProblem.UnsupportedSchema)]
    [InlineData(OmsPrivateRecoveryReadProblem.InvalidRecord)]
    [InlineData(OmsPrivateRecoveryReadProblem.CapacityExceeded)]
    [InlineData(OmsPrivateRecoveryReadProblem.MissingAcceptedFact)]
    [InlineData(OmsPrivateRecoveryReadProblem.OrphanedAcceptedFact)]
    [InlineData(OmsPrivateRecoveryReadProblem.QuarantinedObservation)]
    [InlineData(OmsPrivateRecoveryReadProblem.IntegrityIncident)]
    public async Task EveryReadProblem_PreventsProjectingEvenSuperficiallyConsistentFacts(OmsPrivateRecoveryReadProblem problem)
    {
        var service = new DeribitDurableOrderRecovery(new Fake(Evidence() with { Problems = [problem] }));
        var result = await service.RebuildAsync(Account, Epoch, From, Through, Complete);
        Assert.Null(result.Projection);
        Assert.False(result.Evidence.IsUsable);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task WrongResultBinding_IsRejected(int change)
    {
        var evidence = change switch
        {
            0 => Evidence() with { Scope = new(Account.Tenant, Account.Venue, Account.Environment, new("foreign")) },
            1 => Evidence() with { SnapshotId = Guid.Empty },
            _ => Evidence() with { ReadAtUtc = Through.ToOffset(TimeSpan.FromHours(1)) }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DeribitDurableOrderRecovery(new Fake(evidence)).RebuildAsync(Account, Epoch, From, Through, Complete));
    }

    [Theory]
    [InlineData(0)] [InlineData(3)] [InlineData(99)]
    public async Task UsableClaimCannotHideInvalidDisposition(int disposition)
    {
        var evidence = Evidence() with
        { Observations = [new(Report(), (OmsPrivateCaptureResult)disposition)] };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DeribitDurableOrderRecovery(new Fake(evidence)).RebuildAsync(Account, Epoch, From, Through, Complete));
    }

    [Fact]
    public async Task ValidRead_ComposesTheExistingScopedReducer()
    {
        var result = await new DeribitDurableOrderRecovery(new Fake(Evidence()))
            .RebuildAsync(Account, Epoch, From, Through, Complete);
        Assert.True(result.Projection!.IsConsistent);
        Assert.Equal(Account, result.Projection.Scope);
        Assert.Equal(4, Assert.Single(result.Projection.Orders).KnownFilledNativeAmount);
    }

    [Fact]
    public async Task ReadCancellation_PropagatesWithoutProjectionFallback()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DeribitDurableOrderRecovery(new Fake(Evidence()))
                .RebuildAsync(Account, Epoch, From, Through, Complete, cancel.Token));
    }

    private sealed class Fake(OmsPrivateRecoveryReadResult evidence) : IOmsPrivateRecoveryReader
    {
        public Task<OmsPrivateRecoveryReadResult> ReadAsync(AccountKey scope, int maximumRows = 10000,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(Account, scope);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(evidence);
        }
    }
}
